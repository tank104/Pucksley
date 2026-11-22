using System.Collections.Concurrent;
using System.ComponentModel;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Common;

/// <summary>
/// The command-line programs a Linux desktop already has (pactl, hyprctl, xdg-open, grim…): whether
/// one is installed, and running it. A missing program becomes a message the person can act on
/// rather than a crash.
/// </summary>
internal static class Programs
{
    static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(30);
    static readonly ConcurrentDictionary<string, (string? Path, DateTime At)> Found = new(StringComparer.Ordinal);

    /// <summary>Text output is parsed, so it mustn't be translated; UTF-8 so names survive.</summary>
    public static readonly IReadOnlyDictionary<string, string?> PlainLocale = new Dictionary<string, string?> { ["LC_ALL"] = "C.UTF-8" };

    /// <summary>
    /// Where a program is, or null. Looks along the PATH, then in Omarchy's own folder (a hub started
    /// from a login session's autostart doesn't always inherit the PATH a terminal has). Remembered
    /// for half a minute, so installing something takes effect without restarting the hub.
    /// </summary>
    public static string? Find(string name)
    {
        if (Found.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.At < CacheAge) return hit.Path;
        var path = Search(name, SearchDirectories());
        Found[name] = (path, DateTime.UtcNow);
        return path;
    }

    public static bool Has(string name) => Find(name) is not null;

    /// <summary>The first of <paramref name="names"/> that's installed, or null.</summary>
    public static string? FirstOf(params string[] names) => names.FirstOrDefault(Has);

    internal static string? Search(string name, IEnumerable<string> directories)
    {
        if (name.Contains('/')) return IsRunnable(name) ? name : null;
        foreach (var dir in directories)
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, name);
            if (IsRunnable(candidate)) return candidate;
        }
        return null;
    }

    static IEnumerable<string> SearchDirectories()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return dir;
        if (Environment.GetEnvironmentVariable("OMARCHY_PATH") is { Length: > 0 } omarchy) yield return Path.Combine(omarchy, "bin");
        yield return "/usr/share/omarchy/bin";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/omarchy/bin");
        yield return "/usr/local/bin";
        yield return "/usr/bin";
        yield return "/bin";
    }

    static bool IsRunnable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Forget what was found (tests; after an install).</summary>
    internal static void Forget() => Found.Clear();

    /// <summary>
    /// Runs a program to the end. Throws a <see cref="PluginException"/> that names the program when
    /// it isn't installed; a non-zero exit code is the caller's to judge.
    /// </summary>
    public static async Task<ProcessRunner.Result> RunAsync(string name, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel,
        string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var path = Find(name) ?? throw Missing(name);
        try
        {
            return await ProcessRunner.RunAsync(path, arguments, timeout, cancel, input, environment).ConfigureAwait(false);
        }
        catch (Win32Exception e)
        {
            throw new PluginException($"Couldn't run {name} ({e.Message}).", e);
        }
    }

    /// <summary>Like <see cref="RunAsync"/>, keeping the output as bytes (a picture).</summary>
    public static async Task<ProcessRunner.BinaryResult> RunBinaryAsync(string name, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel,
        string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var path = Find(name) ?? throw Missing(name);
        try
        {
            return await ProcessRunner.RunBinaryAsync(path, arguments, timeout, cancel, input, environment).ConfigureAwait(false);
        }
        catch (Win32Exception e)
        {
            throw new PluginException($"Couldn't run {name} ({e.Message}).", e);
        }
    }

    /// <summary>
    /// Runs a program and returns its output, or throws with the program's own complaint when it
    /// exits with an error.
    /// </summary>
    public static async Task<string> OutputAsync(string name, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel,
        string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await RunAsync(name, arguments, timeout, cancel, input, environment).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Failed(name, result.ExitCode, result.Error.Length > 0 ? result.Error : result.Output);
        return result.Output;
    }

    public static PluginException Missing(string name) => new($"{name} isn't installed on this PC.");

    public static PluginException Failed(string name, int exitCode, string complaint)
    {
        var line = complaint.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return new PluginException(line is null ? $"{name} failed (exit code {exitCode})." : $"{name} failed: {Text.Clip(line, 200)}");
    }
}
