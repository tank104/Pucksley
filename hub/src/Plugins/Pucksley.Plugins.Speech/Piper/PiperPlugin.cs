using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

public sealed class PiperSettings
{
    [Setting("Piper program", Order = 1, Placeholder = @"C:\piper\piper.exe",
        Help = "The piper executable (github.com/rhasspy/piper/releases), or its folder. Empty looks for piper on the PATH.")]
    public string Executable { get; set; } = "";

    [Setting("Voice model", Order = 2, Placeholder = @"C:\piper\voices\en_GB-alba-medium.onnx",
        Help = "A voice's .onnx file, with its .onnx.json next to it. Voices: huggingface.co/rhasspy/piper-voices.")]
    public string Model { get; set; } = "";

    [Setting("Speaker", Order = 3, Advanced = true, Help = "For voices with several speakers: a speaker number or name. Empty for the default.")]
    public string Speaker { get; set; } = "";

    [Setting("Length scale", Order = 4, Advanced = true, Help = "Speaking pace: below 1 is faster, above 1 slower; 1 is the voice's normal pace.")]
    public double LengthScale { get; set; } = 1.0;
}

/// <summary>
/// Piper voices on this computer: one piper process per utterance, text on stdin, raw PCM read from stdout as it's
/// made. The process is killed when synthesis is cancelled (barge-in) or abandoned.
/// </summary>
public sealed class PiperPlugin : SpeechPlugin<PiperSettings>, ITextToSpeech
{
    internal const int DefaultSampleRate = 22050;
    const string Service = "Piper";

    volatile Setup? _setup;
    volatile string? _problem;
    StatusLevel _problemLevel = StatusLevel.Idle;

    public override string Id => "piper";
    public override string Name => "Piper (local voices)";
    public override string Description => "Fast, private voices that run on this computer with Piper. No internet needed.";

    public string EngineId => "piper";
    public string EngineName => "Piper";
    public bool IsReady => _setup is not null;

    /// <summary>Everything needed to run piper, worked out from the settings when the plugin starts.</summary>
    internal sealed record Setup(string Executable, string Model, int SampleRate, int? SpeakerId, double? LengthScale);

    internal sealed record VoiceConfig(int SampleRate, IReadOnlyDictionary<string, int> Speakers);

    public override Task StartAsync(CancellationToken cancel)
    {
        var (setup, level, problem) = Resolve(Settings);
        _setup = setup;
        _problemLevel = level;
        _problem = problem;
        ShowSetup();
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        _setup = null;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        var setup = _setup;
        var speech = SpeechText.Clean(text);
        if (speech.Length == 0) yield break;
        if (setup is null) throw new PluginException($"Piper isn't set up ({_problem ?? "set the program and a voice model"}). Fix it on the hub's Plugins page.");
        cancel.ThrowIfCancellationRequested();

        using var process = Start(setup);
        var errors = new ErrorTail(process);
        using var killOnCancel = cancel.Register(static p => Kill((Process)p!), process);
        try
        {
            // Written while stdout is read, so a long text can't deadlock on full pipes.
            var writing = WriteTextAsync(process, speech);
            int chunks = 0;
            await foreach (var chunk in PcmStream.ReadChunksAsync(process.StandardOutput.BaseStream, setup.SampleRate, Service, cancel).ConfigureAwait(false))
            {
                chunks++;
                yield return chunk;
            }
            await writing.ConfigureAwait(false);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (chunks == 0) throw new PluginException($"Piper made no audio (exit code {process.ExitCode}){errors.Describe()}.");
            if (process.ExitCode != 0) Log.LogWarning("Piper exited with code {Code}{Errors}", process.ExitCode, errors.Describe());
        }
        finally
        {
            Kill(process);  // stopped early or failed: don't leave piper running
        }
    }

    protected override PluginStatus DescribeSetup() => _setup is { } s
        ? new(StatusLevel.Ok, $"Ready: {Path.GetFileNameWithoutExtension(s.Model)} ({s.SampleRate} Hz)")
        : new(_problemLevel, _problem ?? "Set the piper program and a voice model");

    /// <summary>Checks the settings: a <see cref="Setup"/>, or the status level and message saying what's missing or wrong.</summary>
    internal static (Setup? Setup, StatusLevel Level, string? Problem) Resolve(PiperSettings s)
    {
        bool noProgram = string.IsNullOrWhiteSpace(s.Executable), noModel = string.IsNullOrWhiteSpace(s.Model);
        if (noProgram && noModel && FindExecutable(null) is null) return (null, StatusLevel.Idle, "Set the piper program and a voice model");
        if (FindExecutable(s.Executable) is not { } program)
            return (null, StatusLevel.Warning, noProgram ? "piper isn't on the PATH: set the path to the piper program" : $"Can't find the piper program at {Unquote(s.Executable)}");
        if (noModel) return (null, StatusLevel.Idle, "Choose a voice model (.onnx)");
        var model = Unquote(s.Model);
        if (!File.Exists(model)) return (null, StatusLevel.Warning, $"Can't find the voice model {model}");

        VoiceConfig voice;
        try
        {
            voice = ReadVoiceConfig(model);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, StatusLevel.Warning, $"Can't read the voice's {Path.GetFileName(model)}.json, which must sit next to it ({e.Message})");
        }
        var speaker = ResolveSpeaker(s.Speaker, voice.Speakers, out var speakerProblem);
        if (speakerProblem is not null) return (null, StatusLevel.Warning, speakerProblem);
        double? lengthScale = s.LengthScale > 0 && Math.Abs(s.LengthScale - 1.0) > 0.001 ? Math.Clamp(s.LengthScale, 0.1, 5.0) : null;
        return (new Setup(program, model, voice.SampleRate, speaker, lengthScale), StatusLevel.Ok, null);
    }

    /// <summary>The voice's .onnx.json: sample rate (default 22050) and named speakers.</summary>
    internal static VoiceConfig ReadVoiceConfig(string modelPath)
    {
        var json = JsonNode.Parse(File.ReadAllText(modelPath + ".json"));
        int rate = json?["audio"]?["sample_rate"] is JsonValue v && v.TryGetValue<int>(out var r) && r > 0 ? r : DefaultSampleRate;
        var speakers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (json?["speaker_id_map"] is JsonObject map)
        {
            foreach (var (name, id) in map)
                if (id is JsonValue value && value.TryGetValue<int>(out var n)) speakers[name] = n;
        }
        return new VoiceConfig(rate, speakers);
    }

    /// <summary>A speaker number, or a name looked up in the voice's speakers (loosely, as names get typed).</summary>
    internal static int? ResolveSpeaker(string? setting, IReadOnlyDictionary<string, int> speakers, out string? problem)
    {
        problem = null;
        var wanted = setting?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        if (int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0) return id;
        if (NameMatcher.Best(wanted, speakers.Keys) is { } name) return speakers[name];
        problem = speakers.Count == 0
            ? $"This voice has a single speaker, so there's no speaker '{wanted}'"
            : $"This voice has no speaker '{wanted}'. Speakers: {string.Join(", ", speakers.Keys.Take(12))}";
        return null;
    }

    /// <summary>The piper executable: the setting (a file, or a folder containing it), else piper on the PATH.</summary>
    internal static string? FindExecutable(string? setting)
    {
        var name = OperatingSystem.IsWindows() ? "piper.exe" : "piper";
        var path = Unquote(setting);
        try
        {
            if (path.Length > 0)
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
                var inFolder = Path.Combine(path, name);
                return Directory.Exists(path) && File.Exists(inFolder) ? Path.GetFullPath(inFolder) : null;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(Unquote(dir), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            // A malformed path in the setting or on the PATH.
        }
        return null;
    }

    internal static IReadOnlyList<string> BuildArguments(Setup setup)
    {
        var args = new List<string> { "--model", setup.Model, "--output_raw" };
        if (setup.SpeakerId is { } speaker) args.AddRange(["--speaker", speaker.ToString(CultureInfo.InvariantCulture)]);
        if (setup.LengthScale is { } scale) args.AddRange(["--length_scale", scale.ToString("0.###", CultureInfo.InvariantCulture)]);
        return args;
    }

    static Process Start(Setup setup)
    {
        var psi = new ProcessStartInfo(setup.Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),  // a BOM would be read as part of the text
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(setup.Executable) ?? "",  // piper finds espeak-ng-data beside itself
        };
        foreach (var arg in BuildArguments(setup)) psi.ArgumentList.Add(arg);
        try
        {
            return Process.Start(psi) ?? throw new PluginException("Piper didn't start.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            throw new PluginException($"Couldn't start Piper ({e.Message}). Check the program path in its settings.", e);
        }
    }

    static async Task WriteTextAsync(Process process, string text)
    {
        try
        {
            // One line (piper speaks each input line separately), then close so it knows that's all.
            await process.StandardInput.WriteAsync(text + "\n").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // piper exited early; its exit code and error output tell the story.
        }
    }

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    static string Unquote(string? s) => (s ?? "").Trim().Trim('"').Trim();

    /// <summary>The last few lines piper wrote to stderr, for error messages.</summary>
    sealed class ErrorTail
    {
        readonly Queue<string> _lines = new();

        public ErrorTail(Process process)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_lines)
                {
                    _lines.Enqueue(e.Data.Trim());
                    while (_lines.Count > 3) _lines.Dequeue();
                }
            };
            process.BeginErrorReadLine();
        }

        public string Describe()
        {
            lock (_lines) return _lines.Count == 0 ? "" : ": " + string.Join(" / ", _lines);
        }
    }
}
