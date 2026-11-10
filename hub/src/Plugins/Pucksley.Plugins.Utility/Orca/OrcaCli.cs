using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pucksley.Plugins.Orca;

/// <summary>Runs Orca's command-line tool (<c>orca</c>), which talks to the running Orca app.</summary>
public static class OrcaCli
{
    /// <summary>The orca executable: <paramref name="configured"/>, else on PATH, else Orca's usual install folder.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured.Trim()) ? configured.Trim() : null;
        var names = OperatingSystem.IsWindows() ? new[] { "orca.exe" } : ["orca"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        if (OperatingSystem.IsWindows())
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "orca", "resources", "bin", "orca.exe");
            if (File.Exists(installed)) return installed;
        }
        return null;
    }

    /// <summary>Runs <c>orca &lt;args&gt;</c> and parses its JSON answer. Throws <see cref="InvalidOperationException"/> with a readable reason.</summary>
    public static async Task<JsonNode?> RunJsonAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Orca's command-line tool didn't start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(FirstLine(errors) ?? $"Orca's command-line tool exited with code {process.ExitCode}");
            try { return JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { throw new InvalidOperationException(FirstLine(errors) ?? FirstLine(text) ?? "Orca's answer wasn't JSON"); }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException("Orca didn't answer in time; is it running?");
        }
        finally
        {
            if (!process.HasExited) try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
