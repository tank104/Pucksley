using System.ComponentModel;
using System.Diagnostics;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Common;

/// <summary>
/// Starts programs that must outlive the hub and owe it nothing: an app someone asked to open, a
/// file's viewer, the screen locker.
/// </summary>
internal static class Detached
{
    /// <summary>
    /// Starts <paramref name="command"/> in its own session with its input and output closed, and
    /// watches it for <paramref name="watch"/>: a program that can't start usually says so at once.
    /// Returns its exit code if it ended in that time, or null while it's still running (the usual
    /// case for an app).
    /// </summary>
    /// <param name="ownScope">
    /// Also put it in a systemd scope of its own. A hub started at login runs as a systemd unit, and
    /// stopping a unit ends everything started from it — the apps Pucksley opened would close with it.
    /// </param>
    public static async Task<int?> StartAsync(IReadOnlyList<string> command, TimeSpan watch, CancellationToken cancel, bool ownScope = false, string? scopeName = null)
    {
        if (command.Count == 0) throw new ArgumentException("Nothing to start.", nameof(command));
        string program = Programs.Find(command[0]) ?? throw Programs.Missing(command[0]);

        var words = new List<string>();
        // A new session, so Ctrl+C in the terminal the hub was started from doesn't reach it
        if (Programs.Find("setsid") is { } setsid) words.Add(setsid);
        if (ownScope && ScopeCommand(scopeName) is { } scope) words.AddRange(scope);
        words.Add(program);
        words.AddRange(command.Skip(1));

        // The shell only closes the input and output; exec replaces it with the program
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
        psi.ArgumentList.Add("sh");
        foreach (var word in words) psi.ArgumentList.Add(word);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new PluginException($"Couldn't start {command[0]}.");
        }
        catch (Win32Exception e)
        {
            throw new PluginException($"Couldn't start {command[0]} ({e.Message}).", e);
        }
        using (process)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(watch);
            try
            {
                await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                cancel.ThrowIfCancellationRequested();
                return null; // still running: it started
            }
        }
    }

    /// <summary>
    /// <c>systemd-run --user --scope …</c>, when this is a systemd session; null otherwise (the
    /// program then simply stays in the hub's own group).
    /// </summary>
    internal static IReadOnlyList<string>? ScopeCommand(string? name)
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Directory.Exists(Path.Combine(runtime, "systemd"))) return null;
        if (Programs.Find("systemd-run") is not { } systemdRun) return null;
        // "app-<name>-<random>.scope" is how desktops name the apps they start
        string app = new string((name ?? "app").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (app.Length == 0) app = "app";
        if (app.Length > 40) app = app[..40];
        string unit = $"app-pucksley-{app}-{Guid.NewGuid().ToString("n")[..8]}.scope";
        return [systemdRun, "--user", "--scope", "--collect", "--quiet", "--slice=app.slice", $"--unit={unit}", "--"];
    }
}
