using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Actions;

/// <summary>Runs a script action's command in its shell: captures output, enforces the timeout, kills the whole tree when it's exceeded.</summary>
internal static class ScriptRunner
{
    const int MaxCapture = 64 * 1024;

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal sealed record Result(int ExitCode, string Output, string Error, bool TimedOut);

    /// <summary>A process to start, and what to write to its stdin (then closed).</summary>
    internal sealed record Launch(ProcessStartInfo StartInfo, string? Input);

    /// <summary>How to run <paramref name="command"/> (already rendered by <see cref="ShellQuoting"/>) in <paramref name="shell"/>.</summary>
    public static Launch Prepare(ShellKind shell, string command, IReadOnlyDictionary<string, string> environment, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        string? input = null;
        switch (shell)
        {
            case ShellKind.Cmd:
                psi.FileName = "cmd.exe";
                // cmd parses its own command line, so it's passed raw: /s strips just the outer quotes, /v:off keeps
                // ! literal, /d skips AutoRun. chcp makes built-ins (echo, dir) write UTF-8. Lines run one after another.
                var lines = string.Join(" & ", command.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
                psi.Arguments = $"/d /s /v:off /c \"chcp 65001>nul & {lines}\"";
                break;
            case ShellKind.PowerShell or ShellKind.Pwsh:
                psi.FileName = shell == ShellKind.Pwsh || !OperatingSystem.IsWindows() ? "pwsh" : "powershell.exe";
                // -EncodedCommand sidesteps command-line quoting entirely.
                var script = "$ProgressPreference = 'SilentlyContinue'; try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }\n" + command;
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                    psi.ArgumentList.Add(arg);
                break;
            default:
                psi.FileName = shell == ShellKind.Bash ? "bash" : "sh";
                // The script goes in on stdin rather than as -c "...": WSL's bash.exe launcher parses its command line
                // a second time, which would undo the quoting. The braces make the shell read the whole script before
                // running it, and give the commands in it an empty stdin (so one can't swallow the rest of the script).
                psi.ArgumentList.Add("-s");
                input = "{\n" + command.Replace("\r\n", "\n") + "\n} </dev/null\n";
                break;
        }
        foreach (var (key, value) in environment) psi.Environment[key] = value;
        if (shell is ShellKind.Bash or ShellKind.Sh && OperatingSystem.IsWindows() && environment.Count > 0)
        {
            // WSL's bash only sees Windows environment variables listed in WSLENV.
            var existing = Environment.GetEnvironmentVariable("WSLENV");
            psi.Environment["WSLENV"] = string.Join(':', new[] { existing }.Concat(environment.Keys).Where(s => !string.IsNullOrEmpty(s)));
        }
        return new Launch(psi, input);
    }

    public static async Task<Result> RunAsync(Launch launch, TimeSpan timeout, CancellationToken cancel)
    {
        var psi = launch.StartInfo;
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new PluginException($"Couldn't start {psi.FileName}: {e.Message}. Is it installed and on the PATH?", e);
        }
        var output = new CappedReader(process.StandardOutput, MaxCapture);
        var error = new CappedReader(process.StandardError, MaxCapture);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        var feeding = FeedAsync(process, launch.Input, cts.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancel.IsCancellationRequested) throw;
            timedOut = true;
        }
        // Children that outlive the shell can hold the pipes open; don't wait on them for long.
        await Task.WhenAny(Task.WhenAll(output.Completion, error.Completion, feeding), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return new Result(timedOut ? -1 : process.ExitCode, output.Text, error.Text, timedOut);
    }

    /// <summary>Writes the script (when it goes in on stdin) and closes stdin, so a command waiting for input gets EOF.</summary>
    static async Task FeedAsync(Process process, string? input, CancellationToken cancel)
    {
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancel).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the process ended (or was stopped) before reading it all
        }
    }

    static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // already gone
        }
    }

    /// <summary>Drains a stream in the background, keeping the first <c>max</c> characters.</summary>
    sealed class CappedReader
    {
        readonly StringBuilder _text = new();
        readonly int _max;

        public CappedReader(StreamReader reader, int max)
        {
            _max = max;
            Completion = Task.Run(() => PumpAsync(reader));
        }

        public Task Completion { get; }

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int n;
                while ((n = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
                    lock (_text)
                        if (_text.Length < _max) _text.Append(buffer, 0, Math.Min(n, _max - _text.Length));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // the process went away
            }
        }
    }
}
