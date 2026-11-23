using System.Diagnostics;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Common;

/// <summary>Runs a console program (shutdown.exe, powershell.exe, pactl, hyprctl) hidden, capturing its output.</summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>What a program wrote to its standard output, as bytes (a screenshot, a picture from the clipboard).</summary>
    public sealed record BinaryResult(int ExitCode, byte[] Output, string Error);

    /// <summary>
    /// Runs to the end and returns the trimmed output. <paramref name="input"/> is written to the
    /// program's standard input (UTF-8) and <paramref name="environment"/> is added to its
    /// environment (a null value removes a variable). Throws <see cref="System.ComponentModel.Win32Exception"/>
    /// when the program isn't there.
    /// </summary>
    public static async Task<Result> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel,
        string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = StartInfo(fileName, arguments, input is not null, environment);
        psi.StandardOutputEncoding = Encoding.UTF8;

        using var process = Process.Start(psi) ?? throw new PluginException($"Couldn't start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var writing = WriteInputAsync(process, input);
        await WaitAsync(process, fileName, timeout, cancel).ConfigureAwait(false);
        await writing.ConfigureAwait(false);
        return new Result(process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
    }

    /// <summary>Like <see cref="RunAsync"/>, with the standard output kept as bytes.</summary>
    public static async Task<BinaryResult> RunBinaryAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancel,
        string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = StartInfo(fileName, arguments, input is not null, environment);

        using var process = Process.Start(psi) ?? throw new PluginException($"Couldn't start {fileName}.");
        var output = ReadAllAsync(process.StandardOutput.BaseStream);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var writing = WriteInputAsync(process, input);
        await WaitAsync(process, fileName, timeout, cancel).ConfigureAwait(false);
        await writing.ConfigureAwait(false);
        return new BinaryResult(process.ExitCode, await output.ConfigureAwait(false), (await error.ConfigureAwait(false)).Trim());
    }

    static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> arguments, bool hasInput, IReadOnlyDictionary<string, string?>? environment)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = hasInput,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (hasInput) psi.StandardInputEncoding = new UTF8Encoding(false); // a BOM would be read as part of the text
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
        }
        return psi;
    }

    static async Task WriteInputAsync(Process process, string? input)
    {
        if (input is null) return;
        try
        {
            // Written while the output is read, so a long text can't deadlock on full pipes
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program exited without reading it all; its exit code and error output tell the story
        }
    }

    static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        return buffer.ToArray();
    }

    static async Task WaitAsync(Process process, string fileName, TimeSpan timeout, CancellationToken cancel)
    {
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
    }
}
