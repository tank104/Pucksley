using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Pucksley.Sdk;
using Whisper.net;
using Whisper.net.LibraryLoader;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class LocalWhisperTests
{
    /// <summary>Bytes that pass for a ggml model: the magic number, then filler.</summary>
    static byte[] FakeModel(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        "lmgg"u8.CopyTo(bytes);  // 0x67676d6c little-endian
        return bytes;
    }

    [Theory]
    [InlineData("tiny.en", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin")]
    [InlineData("base.en", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin")]
    [InlineData("large-v3-turbo", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin")]
    public void Model_urls(string model, string url) => Assert.Equal(url, LocalWhisperPlugin.ModelUrl(model).AbsoluteUri);

    [Fact]
    public void Models_live_in_the_plugins_data_folder()
    {
        var plugin = Create<LocalWhisperPlugin>(new LocalWhisperSettings(), null, null, out var context);
        Assert.Equal(Path.Combine(context.DataDirectory, "models", "ggml-base.en.bin"), plugin.ModelPath("base.en"));
    }

    [Theory]
    [InlineData("base.en", true)]
    [InlineData("large-v3-turbo", true)]
    [InlineData("large-v3-turbo-q5_0", true)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("a..b", false)]
    [InlineData("Base.EN", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Model_names_are_safe_for_paths_and_urls(string? model, bool valid) => Assert.Equal(valid, LocalWhisperPlugin.IsValidModelName(model));

    [Theory]
    [InlineData("base.en", "auto", "fr-FR", "en")]  // English-only models always get English
    [InlineData("large-v3-turbo", "en", "fr-FR", "en")]
    [InlineData("large-v3-turbo", "auto", "en-NZ", null)]
    [InlineData("large-v3-turbo", "", "mi-NZ", "mi")]
    [InlineData("large-v3-turbo", "", null, null)]
    [InlineData("large-v3-turbo", "xx", null, null)]  // unknown to whisper: detect instead
    public void Language_resolution(string model, string setting, string? hub, string? expected) =>
        Assert.Equal(expected, LocalWhisperPlugin.ResolveLanguage(model, setting, hub, new HashSet<string> { "en", "fr", "mi" }));

    [Theory]
    [InlineData(" Turn on the lights.", "Turn on the lights.")]
    [InlineData(" [BLANK_AUDIO]", "")]
    [InlineData("[MUSIC] Turn it up", "Turn it up")]
    [InlineData(" (wind blowing)", "")]
    [InlineData(" *coughs* ", "")]
    [InlineData("(laughs) Okay then", "(laughs) Okay then")]
    [InlineData("  Hello   there  ", "Hello there")]
    public void Transcripts_lose_whispers_sound_tags(string raw, string expected) => Assert.Equal(expected, LocalWhisperPlugin.CleanTranscript(raw));

    [Fact]
    public void Thread_count_follows_the_setting_or_the_machine()
    {
        Assert.Equal(3, LocalWhisperPlugin.ThreadCount(3));
        Assert.InRange(LocalWhisperPlugin.ThreadCount(0), 1, 8);
    }

    [Fact]
    public async Task Downloads_the_model_once_with_progress_then_renames_it_into_place()
    {
        var model = FakeModel(200_000);
        var bothAsked = new TaskCompletionSource();
        var statuses = new List<string>();
        LocalWhisperPlugin? plugin = null;
        var handler = new RecordingHandler(_ =>
        {
            var body = new TrickleStream(model, [50_000], async (read, cancel) =>
            {
                if (read == 0) await bothAsked.Task.WaitAsync(cancel);
                lock (statuses) statuses.Add(plugin!.Status.Message);
            });
            var response = Responses.Stream(body, "application/octet-stream");
            response.Content.Headers.ContentLength = model.Length;
            return response;
        });
        plugin = Create<LocalWhisperPlugin>(new LocalWhisperSettings { Model = "tiny.en" }, handler, null, out var context);

        var first = plugin.EnsureModelFileAsync("tiny.en");
        var second = plugin.EnsureModelFileAsync("tiny.en");
        Assert.Same(first, second);  // one shared download
        bothAsked.SetResult();
        var path = await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(Path.Combine(context.DataDirectory, "models", "ggml-tiny.en.bin"), path);
        Assert.Equal(model, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(["ggml-tiny.en.bin"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin", handler.Single.Url.AbsoluteUri);
        Assert.Equal(["Downloading tiny.en…", "Downloading tiny.en… 25%", "Downloading tiny.en… 50%", "Downloading tiny.en… 75%", "Downloading tiny.en… 100%"], statuses);

        Assert.Equal(path, await plugin.EnsureModelFileAsync("tiny.en"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_download_that_isnt_a_model_is_thrown_away()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Too many requests</html>") });
        var plugin = Create<LocalWhisperPlugin>(new LocalWhisperSettings { Model = "base.en" }, handler, null, out var context);

        var e = await Assert.ThrowsAsync<PluginException>(() => plugin.EnsureModelFileAsync("base.en"));

        Assert.Equal("Couldn't download the base.en model (the file isn't a Whisper model). Click Download model to try again.", e.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(context.DataDirectory, "models")));
        Assert.Equal(new PluginStatus(StatusLevel.Error, e.Message), plugin.Status);
        Assert.False(plugin.IsReady);  // for a minute, rather than retrying on every utterance
    }

    [Fact]
    public async Task A_missing_model_on_the_server_is_reported()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var plugin = Create<LocalWhisperPlugin>(new LocalWhisperSettings { Model = "huge.en" }, handler, null, out _);

        var e = await Assert.ThrowsAsync<PluginException>(() => plugin.EnsureModelFileAsync("huge.en"));

        Assert.Contains("the server answered 404", e.Message);
    }

    [Fact]
    public async Task A_stalled_download_times_out()
    {
        var handler = new RecordingHandler(_ => Responses.Stream(new TrickleStream(FakeModel(1000), [10], async (read, cancel) =>
        {
            if (read == 1) await Task.Delay(Timeout.Infinite, cancel);
        }), "application/octet-stream"));
        using var http = new HttpClient(handler);
        var file = Path.Combine(Path.GetTempPath(), $"pucksley-stall-{Guid.NewGuid():n}.bin");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ModelDownloader.DownloadAsync(http, new Uri("https://example.com/m.bin"), file, (_, _) => { }, Ct, TimeSpan.FromMilliseconds(200)));
        File.Delete(file);
    }

    [Fact]
    public async Task A_short_download_is_an_error()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = Responses.Stream(new MemoryStream(FakeModel(500)), "application/octet-stream");
            response.Content.Headers.ContentLength = 1000;
            return response;
        });
        using var http = new HttpClient(handler);
        var file = Path.Combine(Path.GetTempPath(), $"pucksley-short-{Guid.NewGuid():n}.bin");

        var e = await Assert.ThrowsAsync<IOException>(() => ModelDownloader.DownloadAsync(http, new Uri("https://example.com/m.bin"), file, (_, _) => { }, Ct));
        Assert.Contains("500 of 1,000 bytes", e.Message);
        File.Delete(file);
    }

    [Fact]
    public async Task Status_before_the_model_is_downloaded()
    {
        var plugin = await StartAsync<LocalWhisperPlugin>(new LocalWhisperSettings());

        Assert.True(plugin.EnabledByDefault);
        Assert.Equal("whisper-local", plugin.EngineId);
        // The native runtime loads here, so this also proves Whisper.net's CPU runtime works on this machine.
        Assert.True(plugin.IsReady, plugin.Status.Message);
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "The base.en model (142 MB) downloads on first use, or click Download model"), plugin.Status);
        Assert.Equal("Download model", Assert.Single(plugin.GetActions()).Label);
    }
}

/// <summary>Real transcription with ggml-tiny.en (downloaded once into %TEMP%) and a clip made by Windows speech synthesis.</summary>
[Trait("Category", "Integration")]
public partial class LocalWhisperIntegrationTests(ITestOutputHelper output)
{
    static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "pucksley-speech-tests", "whisper");

    [Fact]
    public async Task Transcribes_a_spoken_clip_with_tiny_en()
    {
        var clip = await MakeClipAsync("Turn on the kitchen lights and set a timer for ten minutes.", "kitchen.wav");
        var plugin = await StartWithModelAsync();

        var clock = Stopwatch.StartNew();
        var first = await plugin.TranscribeAsync(clip, "en-NZ", Ct);
        var firstTime = clock.Elapsed;
        clock.Restart();
        var warm = await plugin.TranscribeAsync(clip, "en-NZ", Ct);
        var warmTime = clock.Elapsed;

        output.WriteLine($"Runtime: {RuntimeOptions.LoadedLibrary}; {WhisperFactory.GetRuntimeInfo()?.Trim()}");
        output.WriteLine($"Clip {clip.Duration.TotalSeconds:F1} s, {LocalWhisperPlugin.ThreadCount(0)} threads: first call {firstTime.TotalMilliseconds:F0} ms (may include loading), warm {warmTime.TotalMilliseconds:F0} ms");
        output.WriteLine($"Heard: \"{warm}\"");
        Assert.Equal(first, warm);
        var words = Words(warm);
        Assert.Contains("kitchen", words);
        Assert.Contains("timer", words);
        Assert.Contains(words, w => w.StartsWith("light"));
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: tiny.en (CPU)"), plugin.Status);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Hears_a_one_word_answer_shorter_than_a_second()
    {
        var clip = TrimSilence(await MakeClipAsync("Yes.", "yes.wav"));
        Assert.True(clip.Duration < TimeSpan.FromSeconds(1), $"clip is {clip.Duration}");
        var plugin = await StartWithModelAsync();

        var text = await plugin.TranscribeAsync(clip, "en-NZ", Ct);

        // For the record: what whisper.cpp makes of the same clip without the plugin's padding.
        using (var factory = WhisperFactory.FromPath(plugin.ModelPath("tiny.en")))
        await using (var raw = factory.CreateBuilder().WithLanguage("en").Build())
        {
            var unpadded = new List<string>();
            await foreach (var segment in raw.ProcessAsync(clip.Samples.Select(s => s / 32768f).ToArray(), Ct)) unpadded.Add(segment.Text);
            output.WriteLine($"Without padding whisper.cpp heard: \"{string.Concat(unpadded).Trim()}\"");
        }
        output.WriteLine($"Clip {clip.Duration.TotalMilliseconds:F0} ms; plugin heard: \"{text}\"");
        Assert.Contains("yes", Words(text));
        await plugin.StopAsync();
    }

    static AudioClip TrimSilence(AudioClip clip)
    {
        const int threshold = 500;
        int first = Array.FindIndex(clip.Samples, s => Math.Abs((int)s) > threshold);
        int last = Array.FindLastIndex(clip.Samples, s => Math.Abs((int)s) > threshold);
        int margin = clip.SampleRate / 20;
        return new AudioClip(clip.Samples[Math.Max(0, first - margin)..Math.Min(clip.Samples.Length, last + margin)], clip.SampleRate);
    }

    [Fact]
    public async Task Cancelling_or_stopping_mid_transcription_is_safe()
    {
        var phrase = await MakeClipAsync("Turn on the kitchen lights and set a timer for ten minutes.", "kitchen.wav");
        var longClip = new AudioClip(Enumerable.Repeat(phrase.Samples, 8).SelectMany(s => s).ToArray(), phrase.SampleRate);
        var plugin = await StartWithModelAsync();
        await plugin.TranscribeAsync(phrase, "en", Ct);  // loaded and warm

        using (var cts = new CancellationTokenSource())
        {
            var running = plugin.TranscribeAsync(longClip, "en", cts.Token);
            await Task.Delay(200, Ct);
            var clock = Stopwatch.StartNew();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            output.WriteLine($"Cancelled after {clock.ElapsedMilliseconds} ms");
            Assert.InRange(clock.ElapsedMilliseconds, 0, 3000);
        }

        // Stopping (settings change, shutdown) mid-transcription must not free the model under it.
        var inFlight = plugin.TranscribeAsync(longClip, "en", Ct);
        await Task.Delay(200, Ct);
        await plugin.StopAsync();
        var text = await inFlight;
        Assert.Contains("kitchen", Words(text));
    }

    async Task<LocalWhisperPlugin> StartWithModelAsync()
    {
        var plugin = Create<LocalWhisperPlugin>(new LocalWhisperSettings { Model = "tiny.en" }, null, CacheDir, out _);
        await plugin.StartAsync(Ct);
        Assert.True(plugin.IsReady, plugin.Status.Message);
        try
        {
            var clock = Stopwatch.StartNew();
            bool present = File.Exists(plugin.ModelPath("tiny.en"));
            await plugin.EnsureModelFileAsync("tiny.en").WaitAsync(TimeSpan.FromMinutes(10), Ct);
            if (!present) output.WriteLine($"Downloaded tiny.en in {clock.Elapsed.TotalSeconds:F1} s");
        }
        catch (PluginException e) when (e.InnerException is HttpRequestException or TimeoutException or IOException or OperationCanceledException)
        {
            Assert.Skip($"Couldn't download the model (offline?): {e.Message}");
        }
        return plugin;
    }

    static async Task<AudioClip> MakeClipAsync(string text, string fileName)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The test clip is made with Windows speech synthesis.");
        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, fileName);
        if (!File.Exists(path))
        {
            var script =
                "Add-Type -AssemblyName System.Speech; " +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "$f = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono); " +
                $"$s.SetOutputToWaveFile('{path}', $f); $s.Speak('{text}'); $s.Dispose()";
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in (string[])["-NoProfile", "-NonInteractive", "-Command", script]) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var errors = await process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            Assert.SkipWhen(process.ExitCode != 0 || !File.Exists(path), $"Windows speech synthesis isn't available: {errors}");
        }
        return Wav.Decode(await File.ReadAllBytesAsync(path, Ct));
    }

    static string[] Words(string text) => NonLetters().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToArray();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonLetters();
}
