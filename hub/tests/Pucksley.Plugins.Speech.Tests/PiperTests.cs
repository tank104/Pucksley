using System.Diagnostics;
using System.Text;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class PiperTests
{
    [Fact]
    public void Arguments_follow_the_piper_command_line()
    {
        var setup = new PiperPlugin.Setup(@"C:\piper\piper.exe", @"C:\voices\en_GB-alba-medium.onnx", 22050, 3, 0.9);
        Assert.Equal(["--model", @"C:\voices\en_GB-alba-medium.onnx", "--output_raw", "--speaker", "3", "--length_scale", "0.9"], PiperPlugin.BuildArguments(setup));
        Assert.Equal(["--model", "v.onnx", "--output_raw"], PiperPlugin.BuildArguments(setup with { Model = "v.onnx", SpeakerId = null, LengthScale = null }));
    }

    [Fact]
    public void Voice_config_gives_the_sample_rate_and_speakers()
    {
        using var dir = new TempDir();
        var model = dir.Write("multi.onnx", "");
        dir.Write("multi.onnx.json", """{"audio":{"sample_rate":16000,"quality":"medium"},"num_speakers":2,"speaker_id_map":{"alice":0,"bob":1}}""");
        var plain = dir.Write("plain.onnx", "");
        dir.Write("plain.onnx.json", """{"espeak":{"voice":"en-gb"}}""");

        var multi = PiperPlugin.ReadVoiceConfig(model);
        Assert.Equal(16000, multi.SampleRate);
        Assert.Equal(1, multi.Speakers["Bob"]);
        Assert.Equal(PiperPlugin.DefaultSampleRate, PiperPlugin.ReadVoiceConfig(plain).SampleRate);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("1", 1, null)]
    [InlineData("bob", 1, null)]
    [InlineData("Alice", 0, null)]
    [InlineData("zed", null, "This voice has no speaker 'zed'. Speakers: alice, bob")]
    public void Speakers_resolve_by_number_or_name(string setting, int? id, string? problem)
    {
        var speakers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["alice"] = 0, ["bob"] = 1 };
        Assert.Equal(id, PiperPlugin.ResolveSpeaker(setting, speakers, out var actualProblem));
        Assert.Equal(problem, actualProblem);
    }

    [Fact]
    public async Task Status_says_what_is_missing()
    {
        using var dir = new TempDir();
        var program = dir.Write(OperatingSystem.IsWindows() ? "piper.exe" : "piper", "");

        var unset = await StartAsync<PiperPlugin>(new PiperSettings());
        Assert.True(unset.EnabledByDefault);
        Assert.False(unset.IsReady);
        Assert.Equal(StatusLevel.Idle, unset.Status.Level);

        var noModel = await StartAsync<PiperPlugin>(new PiperSettings { Executable = dir.Path });  // a folder with piper in it works too
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Choose a voice model (.onnx)"), noModel.Status);

        var missing = await StartAsync<PiperPlugin>(new PiperSettings { Executable = program, Model = Path.Combine(dir.Path, "nope.onnx") });
        Assert.False(missing.IsReady);
        Assert.Equal(StatusLevel.Warning, missing.Status.Level);
        Assert.StartsWith("Can't find the voice model", missing.Status.Message);

        var noJson = await StartAsync<PiperPlugin>(new PiperSettings { Executable = program, Model = dir.Write("v.onnx", "") });
        Assert.False(noJson.IsReady);
        Assert.Contains("v.onnx.json", noJson.Status.Message);
        await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(noJson.SynthesizeAsync("Hello", Ct)));

        dir.Write("v.onnx.json", """{"audio":{"sample_rate":22050}}""");
        var ready = await StartAsync<PiperPlugin>(new PiperSettings { Executable = program, Model = Path.Combine(dir.Path, "v.onnx") });
        Assert.True(ready.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: v (22050 Hz)"), ready.Status);
    }

    [Fact]
    public async Task Speaks_through_piper_reading_stdout_as_it_arrives()
    {
        using var piper = FakePiper.Create(hang: false);
        var plugin = await StartAsync<PiperPlugin>(new PiperSettings { Executable = piper.Program, Model = piper.Model, Speaker = "Bob", LengthScale = 0.9 });
        Assert.True(plugin.IsReady, plugin.Status.Message);

        var chunks = await Audio.CollectAsync(plugin.SynthesizeAsync("Kia ora, *Māori* world", Ct));

        Assert.Equal(piper.Samples, Audio.Join(chunks));
        Assert.All(chunks, c => Assert.Equal(22050, c.SampleRate));
        var args = File.ReadAllText(piper.File("args.txt")).Trim();
        Assert.Equal($"--model {piper.Model} --output_raw --speaker 1 --length_scale 0.9", args);
        // UTF-8 without a BOM, one line, markdown gone.
        Assert.Equal(Encoding.UTF8.GetBytes("Kia ora, Māori world\n"), File.ReadAllBytes(piper.File("stdin.bin")));
    }

    [Fact]
    public async Task Cancelling_kills_piper()
    {
        using var piper = FakePiper.Create(hang: true);
        var plugin = await StartAsync<PiperPlugin>(new PiperSettings { Executable = piper.Program, Model = piper.Model });
        using var cts = new CancellationTokenSource();

        var clock = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in plugin.SynthesizeAsync("A long story.", cts.Token))
            {
                clock.Start();
                cts.Cancel();  // barge-in after the first audio
            }
        });
        Assert.InRange(clock.ElapsedMilliseconds, 0, 3000);

        var pid = int.Parse(File.ReadAllText(piper.File("pid.txt")).Trim());
        var deadline = Stopwatch.StartNew();
        while (IsRunning(pid) && deadline.ElapsedMilliseconds < 5000) await Task.Delay(50, Ct);
        Assert.False(IsRunning(pid), "piper's child process is still running");
    }

    static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pucksley-piper-" + Guid.NewGuid().ToString("n"))).FullName;

        public string Write(string name, string content)
        {
            var file = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(file, content);
            return file;
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A just-killed process may still hold a file for a moment; it's only %TEMP%.
            }
        }
    }

    /// <summary>
    /// A stand-in for piper (Windows: a .cmd that hands off to PowerShell): records its arguments, stdin and PID,
    /// then writes raw PCM to stdout in two parts, optionally hanging in between like a slow synthesis.
    /// </summary>
    sealed class FakePiper : IDisposable
    {
        readonly TempDir _dir;

        FakePiper(TempDir dir, short[] samples)
        {
            _dir = dir;
            Samples = samples;
        }

        public string Program => _dir.File("piper.cmd");
        public string Model => _dir.File("voice.onnx");
        public short[] Samples { get; }
        public string File(string name) => _dir.File(name);

        public static FakePiper Create(bool hang)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "The fake piper is a Windows batch file.");
            var samples = Audio.Signal(22050 / 2 + 3);
            var dir = new TempDir();
            System.IO.File.WriteAllBytes(dir.File("audio.raw"), Audio.ToBytes(samples));
            if (hang) dir.Write("hang", "");
            dir.Write("voice.onnx", "");
            dir.Write("voice.onnx.json", """{"audio":{"sample_rate":22050},"num_speakers":2,"speaker_id_map":{"alice":0,"bob":1}}""");
            dir.Write("fake.ps1", """
                $stdin = [Console]::OpenStandardInput()
                $ms = New-Object System.IO.MemoryStream
                $stdin.CopyTo($ms)
                [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'stdin.bin'), $ms.ToArray())
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'pid.txt'), "$PID")
                $bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'audio.raw'))
                $out = [Console]::OpenStandardOutput()
                $half = [int]($bytes.Length / 2) + 1
                $out.Write($bytes, 0, $half)
                $out.Flush()
                if (Test-Path (Join-Path $PSScriptRoot 'hang')) { Start-Sleep -Seconds 60 }
                $out.Write($bytes, $half, $bytes.Length - $half)
                $out.Flush()
                """);
            dir.Write("piper.cmd", "@echo off\r\necho %*> \"%~dp0args.txt\"\r\npowershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fake.ps1\"\r\n");
            return new FakePiper(dir, samples);
        }

        public void Dispose() => _dir.Dispose();
    }
}
