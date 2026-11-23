using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Audio;

/// <summary>
/// The PC's mixer through <c>pactl</c>, which talks to PipeWire (pipewire-pulse) and PulseAudio
/// alike: the default output's volume and mute, the outputs, and each app's streams. A
/// <c>pactl subscribe</c> running alongside reports changes made elsewhere (the volume keys, a
/// headset connecting).
/// </summary>
/// <remarks>Every call blocks for a few milliseconds: call from pool threads (<see cref="Blocking.RunAsync{T}"/>).</remarks>
internal sealed class PulseAudio : IPcAudio
{
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    readonly ILogger _log;
    readonly IPactl _pactl;
    readonly CancellationTokenSource _stop = new();
    Task? _watching;
    int _started;

    public event Action? Changed;

    public PulseAudio(ILogger log, IPactl? pactl = null)
    {
        _log = log;
        _pactl = pactl ?? new Pactl();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // Fails here, with the reason, when there's no pactl or no sound server
        _pactl.Run(CallTimeout, "info");
        _watching = Task.Run(() => WatchAsync(_stop.Token));
    }

    // ───────────────────────── Master volume

    public MasterVolume? GetMaster()
    {
        var (shown, level) = DefaultSink(ReadSinks());
        return shown is null || level is null ? null : new MasterVolume(PulseNames.Output(shown), level.Volume, level.Muted);
    }

    public MasterVolume SetMaster(int? volume, bool? muted)
    {
        var (_, level) = DefaultSink(ReadSinks());
        if (level is null) throw new PluginException("This PC has no audio output.");
        if (volume is int v) _pactl.Run(CallTimeout, "set-sink-volume", level.Name, Percent(v));
        if (muted is bool m) _pactl.Run(CallTimeout, "set-sink-mute", level.Name, m ? "1" : "0");
        return GetMaster() ?? throw new PluginException("This PC has no audio output.");
    }

    /// <summary>
    /// The output in use, and the one its loudness lives on. They differ when sound goes through a
    /// processing stage first (Omarchy's speaker tuning, EasyEffects): turning that one down would
    /// change what goes into the processing, not how loud the speakers are.
    /// </summary>
    (PulseSink? Shown, PulseSink? Level) DefaultSink(List<PulseSink> sinks)
    {
        string name = _pactl.Run(CallTimeout, "get-default-sink").Trim();
        var shown = sinks.FirstOrDefault(s => s.Name == name);
        if (shown is null) return (null, null);
        var level = shown;
        if (!IsHardware(name) && _pactl.ResolveLevelSink(name) is { } resolved && sinks.FirstOrDefault(s => s.Name == resolved) is { } real) level = real;
        return (shown, level);
    }

    static bool IsHardware(string sinkName) =>
        sinkName.StartsWith("alsa_output.", StringComparison.Ordinal) || sinkName.StartsWith("bluez_output.", StringComparison.Ordinal);

    List<PulseSink> ReadSinks() => PulseJson.Sinks(_pactl.Run(CallTimeout, "-f", "json", "list", "sinks"));

    // ───────────────────────── Outputs

    public IReadOnlyList<AudioOutput> GetOutputs()
    {
        var sinks = ReadSinks();
        string current = _pactl.Run(CallTimeout, "get-default-sink").Trim();
        // The speakers behind a processing stage aren't a choice of their own: picking them would only bypass it
        string? hidden = _pactl.FrontedSink();
        return sinks
            .Where(s => s.Name == current || (s.Usable && s.Name != hidden))
            .Select(s => new AudioOutput(s.Name, PulseNames.Output(s), s.Name == current))
            .ToList();
    }

    public void SetDefaultOutput(string deviceId)
    {
        _pactl.Run(CallTimeout, "set-default-sink", deviceId);
        // Apps that are already playing follow. Streams without an app name are a processing
        // stage's own plumbing (and EasyEffects' output is the same thing): moving those would
        // rewire the processing itself.
        foreach (var stream in ReadStreams())
        {
            if (!IsAppStream(stream)) continue;
            try
            {
                _pactl.Run(CallTimeout, "move-sink-input", stream.Index.ToString(CultureInfo.InvariantCulture), deviceId);
            }
            catch (PluginException e)
            {
                _log.LogDebug("Couldn't move stream {Index}: {Message}", stream.Index, e.Message); // it ended meanwhile
            }
        }
    }

    // ───────────────────────── Apps

    public IReadOnlyList<AppAudio> GetApps() => Group(ReadStreams());

    public AppAudio? SetApp(string key, int? volume, bool? muted)
    {
        foreach (var stream in ReadStreams().Where(s => IsAppStream(s) && PulseNames.App(s).Key == key))
        {
            string index = stream.Index.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (volume is int v) _pactl.Run(CallTimeout, "set-sink-input-volume", index, Percent(v));
                if (muted is bool m) _pactl.Run(CallTimeout, "set-sink-input-mute", index, m ? "1" : "0");
            }
            catch (PluginException e)
            {
                _log.LogDebug("Couldn't change stream {Index}: {Message}", stream.Index, e.Message); // it ended meanwhile
            }
        }
        return GetApps().FirstOrDefault(a => a.Key == key);
    }

    List<PulseStream> ReadStreams() => PulseJson.Streams(_pactl.Run(CallTimeout, "-f", "json", "list", "sink-inputs"));

    static bool IsAppStream(PulseStream stream) =>
        stream.Property("application.name") is { } name && !name.Equals("EasyEffects", StringComparison.OrdinalIgnoreCase);

    /// <summary>One entry per app: a browser plays each tab as its own stream.</summary>
    internal static List<AppAudio> Group(IEnumerable<PulseStream> streams) =>
        streams.Where(IsAppStream)
            .Select(s => (Stream: s, App: PulseNames.App(s)))
            .GroupBy(x => x.App.Key)
            .Select(g => new AppAudio(g.Key, g.First().App.Name, g.First().App.ProcessName,
                g.Max(x => x.Stream.Volume), g.All(x => x.Stream.Muted), g.Any(x => !x.Stream.Corked)))
            .OrderByDescending(a => a.Playing)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string Percent(int volume) => Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture) + "%";

    // ───────────────────────── Changes made elsewhere

    /// <summary>
    /// Follows <c>pactl subscribe</c>. It ends when the sound server restarts, so it's started again
    /// (after a pause that grows while there's no server to talk to).
    /// </summary>
    async Task WatchAsync(CancellationToken stop)
    {
        var pause = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            var began = DateTime.UtcNow;
            try
            {
                await _pactl.SubscribeAsync(line =>
                {
                    if (IsMasterEvent(line)) Changed?.Invoke();
                }, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "pactl subscribe stopped");
            }
            if (stop.IsCancellationRequested) return;
            // Whatever happened while nobody was listening
            Changed?.Invoke();
            pause = DateTime.UtcNow - began > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 30));
            try { await Task.Delay(pause, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// "Event 'change' on sink #63" (a volume or mute), "Event 'change' on server" (another default
    /// output), "Event 'new' on sink #70" (one appeared). Streams and sources come and go all the
    /// time and don't move the master volume.
    /// </summary>
    internal static bool IsMasterEvent(string line)
    {
        int on = line.IndexOf(" on ", StringComparison.Ordinal);
        if (on < 0) return false;
        var what = line.AsSpan(on + 4).Trim();
        return what.StartsWith("sink #", StringComparison.Ordinal) || what.StartsWith("server", StringComparison.Ordinal) || what.StartsWith("card #", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _watching?.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* it was cancelled */ }
    }
}

/// <summary>The <c>pactl</c> program (and Omarchy's two helpers for sound that goes through processing first).</summary>
internal interface IPactl
{
    /// <summary>Runs pactl and returns what it printed. Throws <see cref="PluginException"/> when it fails.</summary>
    string Run(TimeSpan timeout, params string[] arguments);

    /// <summary>Runs <c>pactl subscribe</c>, handing over each line, until cancelled or the server goes away.</summary>
    Task SubscribeAsync(Action<string> onLine, CancellationToken cancel);

    /// <summary>For an output that is a processing stage: the hardware output its sound ends up on, or null.</summary>
    string? ResolveLevelSink(string sinkName);

    /// <summary>The hardware output a processing stage sits in front of right now, or null.</summary>
    string? FrontedSink();
}

internal sealed class Pactl : IPactl
{
    static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(3);

    public string Run(TimeSpan timeout, params string[] arguments)
    {
        if (!Programs.Has("pactl"))
            throw new PluginException("pactl isn't installed, so this PC's sound can't be reached. Install it (it comes with libpulse or pulseaudio-utils).");
        var result = Programs.RunAsync("pactl", arguments, timeout, CancellationToken.None, environment: Programs.PlainLocale).GetAwaiter().GetResult();
        if (result.ExitCode == 0) return result.Output;
        string complaint = result.Error.Length > 0 ? result.Error : result.Output;
        if (complaint.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || complaint.Contains("Connection failure", StringComparison.OrdinalIgnoreCase))
            throw new PluginException("This PC's sound server (PipeWire or PulseAudio) isn't running.");
        throw Programs.Failed("pactl", result.ExitCode, complaint);
    }

    public async Task SubscribeAsync(Action<string> onLine, CancellationToken cancel)
    {
        var path = Programs.Find("pactl") ?? throw Programs.Missing("pactl");
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("subscribe");
        psi.Environment["LC_ALL"] = "C.UTF-8";
        using var process = Process.Start(psi) ?? throw new PluginException("Couldn't start pactl.");
        using var kill = cancel.Register(() =>
        {
            try { process.Kill(); } catch (Exception) { /* already gone */ }
        });
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancel).ConfigureAwait(false) is { } line) onLine(line);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch (Exception) { /* already gone */ }
        }
        cancel.ThrowIfCancellationRequested();
    }

    public string? ResolveLevelSink(string sinkName) => Helper("omarchy-audio-output-sink", sinkName);

    public string? FrontedSink() => Helper("omarchy-audio-tuning", "fronted-sink");

    static string? Helper(string program, params string[] arguments)
    {
        if (!Programs.Has(program)) return null;
        try
        {
            var result = Programs.RunAsync(program, arguments, HelperTimeout, CancellationToken.None).GetAwaiter().GetResult();
            var line = result.Output.Split('\n').FirstOrDefault()?.Trim();
            return result.ExitCode == 0 && !string.IsNullOrEmpty(line) ? line : null;
        }
        catch (PluginException)
        {
            return null;
        }
    }
}
