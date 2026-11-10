using System.Diagnostics;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>Runs a console program (shutdown.exe, powershell.exe) hidden, capturing its output.</summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error);

    public static async Task<Result> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new PluginException($"Couldn't start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            cancel.ThrowIfCancellationRequested();
            throw new PluginException($"{Path.GetFileNameWithoutExtension(fileName)} took too long.");
        }
        return new Result(process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
    }

    /// <summary>
    /// Runs a script in Windows PowerShell 5.1 (always present; has Get-WmiObject and Get-StartApps)
    /// with UTF-8 output. The script goes base64-encoded, so no command-line quoting can break it.
    /// </summary>
    public static Task<Result> PowerShellAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        var full = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, cancel);
    }
}
