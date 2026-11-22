using System.Text;
using Pucksley.Plugins.Pc.Common;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>Windows PowerShell, for the few things only it can reach (WMI brightness, Get-StartApps).</summary>
internal static class PowerShell
{
    /// <summary>
    /// Runs a script in Windows PowerShell 5.1 (always present; has Get-WmiObject and Get-StartApps)
    /// with UTF-8 output. The script goes base64-encoded, so no command-line quoting can break it.
    /// </summary>
    public static Task<ProcessRunner.Result> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        var full = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, cancel);
    }
}
