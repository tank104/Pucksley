using System.Globalization;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// The PC's vital signs, read from /proc and /sys (the same on every distribution) and said by
/// <see cref="SystemFacts"/> (the same as on Windows).
/// </summary>
internal static class SystemInfo
{
    /// <summary>Reads a small text file, or null when it isn't there.</summary>
    static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<string> DescribeAsync(CancellationToken cancel)
    {
        int cpu = await CpuPercentAsync(cancel).ConfigureAwait(false);
        return new SystemFacts
        {
            Machine = Environment.MachineName,
            OsVersion = OsVersion(Read("/etc/os-release") ?? Read("/usr/lib/os-release"), Read("/proc/sys/kernel/osrelease")),
            CpuPercent = cpu,
            Processor = ProcessorName(Read("/proc/cpuinfo")),
            Memory = Memory(Read("/proc/meminfo")),
            Drive = RootDrive(),
            Uptime = Uptime(Read("/proc/uptime")),
            Battery = Battery("/sys/class/power_supply"),
        }.Describe();
    }

    /// <summary>Busy share of all cores over a short sample.</summary>
    static async Task<int> CpuPercentAsync(CancellationToken cancel)
    {
        if (CpuTimes(Read("/proc/stat")) is not { } first) return 0;
        await Task.Delay(300, cancel).ConfigureAwait(false);
        if (CpuTimes(Read("/proc/stat")) is not { } second) return 0;
        return CpuPercent(first, second);
    }

    /// <summary>The first line of /proc/stat, "cpu  user nice system idle iowait irq softirq steal …": (idle, total) in ticks.</summary>
    internal static (long Idle, long Total)? CpuTimes(string? stat)
    {
        var line = stat?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var numbers = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(n => long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0).ToArray();
        if (numbers.Length < 4) return null;
        long idle = numbers[3] + (numbers.Length > 4 ? numbers[4] : 0); // waiting for the disk is idle too
        return (idle, numbers.Take(8).Sum());
    }

    internal static int CpuPercent((long Idle, long Total) first, (long Idle, long Total) second)
    {
        long total = second.Total - first.Total, idle = second.Idle - first.Idle;
        return total <= 0 ? 0 : (int)Math.Clamp(Math.Round(100.0 * (total - idle) / total), 0, 100);
    }

    /// <summary>"Omarchy 4.5.0 (Linux 6.12.1)", "Ubuntu 24.04.1 LTS (Linux 6.8.0-45-generic)".</summary>
    internal static string OsVersion(string? osRelease, string? kernel)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (osRelease ?? "").Split('\n'))
        {
            int equals = raw.IndexOf('=');
            if (equals > 0) values[raw[..equals].Trim()] = raw[(equals + 1)..].Trim().Trim('"', '\'');
        }
        string name = values.GetValueOrDefault("PRETTY_NAME") is { Length: > 0 } pretty ? pretty : values.GetValueOrDefault("NAME") is { Length: > 0 } plain ? plain : "Linux";
        // A rolling distribution's pretty name leaves its release out
        string? release = values.GetValueOrDefault("VERSION_ID") is { Length: > 0 } version ? version
            : values.GetValueOrDefault("BUILD_ID") is { Length: > 0 } build && build != "rolling" ? build : null;
        if (release is not null && !name.Contains(release, StringComparison.Ordinal)) name += $" {release}";
        return string.IsNullOrWhiteSpace(kernel) ? name : $"{name} (Linux {kernel.Trim()})";
    }

    /// <summary>"model name : AMD Ryzen 7 7840U" (x86), or "Model : Raspberry Pi 5" where the chip isn't named.</summary>
    internal static string? ProcessorName(string? cpuinfo)
    {
        if (cpuinfo is null) return null;
        foreach (var key in new[] { "model name", "Hardware", "Model", "cpu model" })
        {
            foreach (var line in cpuinfo.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase) && line[(colon + 1)..].Trim() is { Length: > 0 } value)
                    return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        return null;
    }

    /// <summary>From /proc/meminfo's MemTotal and MemAvailable (in kB).</summary>
    internal static string? Memory(string? meminfo)
    {
        if (meminfo is null) return null;
        long Kb(string key)
        {
            var line = meminfo.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            var number = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        }
        long total = Kb("MemTotal"), available = Kb("MemAvailable");
        if (total <= 0) return null;
        const double KbPerGb = 1048576.0;
        return SystemFacts.MemoryText((total - available) / KbPerGb, total / KbPerGb, (int)Math.Round(100.0 * (total - available) / total));
    }

    static string? RootDrive()
    {
        try
        {
            var drive = new DriveInfo("/");
            return SystemFacts.DriveText("Disk", drive.AvailableFreeSpace / 1073741824.0, drive.TotalSize / 1073741824.0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static TimeSpan Uptime(string? uptime)
    {
        var seconds = uptime?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? TimeSpan.FromSeconds(s) : TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    /// <summary>
    /// From /sys/class/power_supply: the PC's own battery (a wireless mouse reports one too, marked
    /// as a device's), and whether it's on mains power.
    /// </summary>
    internal static string Battery(string powerSupplyFolder)
    {
        if (!Directory.Exists(powerSupplyFolder)) return "No battery";
        string? battery = null;
        bool mains = false;
        foreach (var supply in Directory.EnumerateDirectories(powerSupplyFolder).OrderBy(d => d, StringComparer.Ordinal))
        {
            string type = Read(Path.Combine(supply, "type"))?.Trim() ?? "";
            if (type == "Battery" && battery is null && Read(Path.Combine(supply, "scope"))?.Trim() != "Device") battery = supply;
            else if (type != "Battery" && Read(Path.Combine(supply, "online"))?.Trim() == "1") mains = true;
        }
        if (battery is null) return "No battery";

        long? Number(string file) => long.TryParse(Read(Path.Combine(battery, file))?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
        int? percent = Number("capacity") is long capacity ? (int)Math.Clamp(capacity, 0, 100) : null;
        string status = Read(Path.Combine(battery, "status"))?.Trim() ?? "";
        // What's left over how fast it's going: energy (µWh ÷ µW) or charge (µAh ÷ µA)
        TimeSpan? remaining = null;
        long? left = Number("energy_now") ?? Number("charge_now"), rate = Number("power_now") ?? Number("current_now");
        if (status == "Discharging" && left is > 0 && rate is > 0) remaining = TimeSpan.FromHours((double)left / rate.Value);
        return SystemFacts.BatteryText(percent, charging: status == "Charging", pluggedIn: mains || status is "Full" or "Not charging", remaining);
    }
}
