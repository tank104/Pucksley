using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Pucksley.Core.Pucks;

/// <summary>
/// Watches for pucks plugged in over USB (the ESP32-S3's own USB: VID 303A, PID 1001) and hands
/// each one to the <see cref="PuckManager"/>. Windows: registry; Linux: /sys/class/tty.
/// </summary>
public sealed class SerialScanner(PuckManager manager, ILogger<SerialScanner> log) : IAsyncDisposable
{
    const string Vid = "303A", Pid = "1001";
    readonly Dictionary<string, SerialPuckConnection> _open = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _notPuck = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    public bool Enabled { get; set; } = true;

    public void Start() => _loop = Task.Run(() => LoopAsync(_stop.Token));

    async Task LoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (Enabled) Scan();
                await Task.Delay(2500, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogDebug(e, "USB scan failed"); }
        }
    }

    void Scan()
    {
        var ports = FindPuckPorts();
        foreach (var (port, c) in _open.ToList())
        {
            if (!c.IsClosed) continue;
            _open.Remove(port);
            // Other ESP32-S3 boards share the puck's USB ids. One whose link ended without a hello
            // isn't a puck: leave its port alone until it's unplugged, rather than knocking every few seconds.
            if (!c.SaidHello && _notPuck.Add(port))
                log.LogInformation("{Port} didn't answer as a puck; leaving it alone until it's unplugged", port);
        }
        _notPuck.RemoveWhere(p => !ports.Contains(p, StringComparer.OrdinalIgnoreCase));
        foreach (var port in ports)
        {
            if (_open.ContainsKey(port) || _notPuck.Contains(port)) continue;
            if (_failedAt.TryGetValue(port, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(15)) continue;
            try
            {
                var c = SerialPuckConnection.Open(port);
                _open[port] = c;
                manager.Attach(c);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Busy (a flasher or a serial monitor has it) or gone: try again later
                _failedAt[port] = DateTime.UtcNow;
                log.LogDebug("Couldn't open {Port}: {Message}", port, e.Message);
            }
        }
    }

    public static IReadOnlyList<string> FindPuckPorts()
    {
        if (OperatingSystem.IsWindows()) return WindowsPorts();
        if (OperatingSystem.IsLinux()) return LinuxPorts();
        return [];
    }

    static IReadOnlyList<string> WindowsPorts()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var present = new HashSet<string>(SafePortNames(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return result;
        foreach (var device in usb.GetSubKeyNames())
        {
            if (!device.StartsWith($"VID_{Vid}&PID_{Pid}", StringComparison.OrdinalIgnoreCase)) continue;
            using var deviceKey = usb.OpenSubKey(device);
            if (deviceKey is null) continue;
            foreach (var instance in deviceKey.GetSubKeyNames())
            {
                using var parameters = deviceKey.OpenSubKey($@"{instance}\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && present.Contains(port) && !result.Contains(port, StringComparer.OrdinalIgnoreCase))
                    result.Add(port);
            }
        }
        return result;
    }

    static IReadOnlyList<string> LinuxPorts()
    {
        var result = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/tty", "ttyACM*"))
            {
                // /sys/class/tty/ttyACM0/device -> the USB interface; idVendor/idProduct sit one level up
                var dev = Path.Combine(dir, "device");
                string? vid = ReadUp(dev, "idVendor"), pid = ReadUp(dev, "idProduct");
                if (vid?.Equals(Vid, StringComparison.OrdinalIgnoreCase) == true && pid?.Equals(Pid, StringComparison.OrdinalIgnoreCase) == true)
                    result.Add("/dev/" + Path.GetFileName(dir));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;

        static string? ReadUp(string start, string file)
        {
            var d = new DirectoryInfo(start);
            for (int i = 0; i < 4 && d is not null; i++, d = d.Parent)
            {
                var f = Path.Combine(d.FullName, file);
                if (File.Exists(f)) return File.ReadAllText(f).Trim();
            }
            return null;
        }
    }

    static string[] SafePortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch (Exception) { return []; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch { }
        foreach (var c in _open.Values) c.Close("hub stopping");
    }
}
