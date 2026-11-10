using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>The PC's vital signs, as one short paragraph.</summary>
internal static class SystemInfo
{
    public static async Task<string> DescribeAsync(CancellationToken cancel)
    {
        int cpu = await CpuPercentAsync(cancel).ConfigureAwait(false);
        var parts = new List<string> { $"{Environment.MachineName}: {WindowsVersion()}" };
        string? processor = ProcessorName();
        parts.Add($"CPU {cpu}%{(processor is null ? "" : $" ({processor})")}");
        if (Memory() is { } memory) parts.Add(memory);
        if (SystemDrive() is { } drive) parts.Add(drive);
        parts.Add($"Up {Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        var ips = LanTools.LocalIPv4Addresses().Take(3).Select(a => a.ToString()).ToList();
        parts.Add(ips.Count == 0 ? "No network address" : $"IP {string.Join(", ", ips)}");
        parts.Add(Battery());
        return string.Join(". ", parts) + ".";
    }

    /// <summary>Busy share of all cores over a short sample.</summary>
    static async Task<int> CpuPercentAsync(CancellationToken cancel)
    {
        if (!GetSystemTimes(out long idle1, out long kernel1, out long user1)) return 0;
        await Task.Delay(300, cancel).ConfigureAwait(false);
        if (!GetSystemTimes(out long idle2, out long kernel2, out long user2)) return 0;
        long idle = idle2 - idle1, total = (kernel2 - kernel1) + (user2 - user1); // kernel time includes idle time
        return total <= 0 ? 0 : (int)Math.Round(100.0 * (total - idle) / total);
    }

    /// <summary>"Windows 11 Pro 25H2 (build 26200.1234)".</summary>
    internal static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = key?.GetValue("ProductName") as string ?? "Windows";
        string? display = key?.GetValue("DisplayVersion") as string;
        string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        string ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
        // Windows 11 still says "Windows 10" in ProductName
        if (int.TryParse(build, out int number) && number >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return $"{product}{(display is null ? "" : $" {display}")} (build {build}{ubr})";
    }

    static string? ProcessorName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name ? name : null;
    }

    static string? Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        double total = status.ullTotalPhys / 1073741824.0, used = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
        return $"Memory {used:0.0} of {total:0.0} GB used ({status.dwMemoryLoad}%)";
    }

    static string? SystemDrive()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return $"{drive.Name.TrimEnd('\\')} {drive.AvailableFreeSpace / 1073741824.0:0} GB free of {drive.TotalSize / 1073741824.0:0} GB";
        }
        catch (IOException)
        {
            return null;
        }
    }

    static string Battery()
    {
        if (!GetSystemPowerStatus(out var power) || (power.BatteryFlag & 128) != 0 || power.BatteryFlag == 255) return "No battery";
        string percent = power.BatteryLifePercent <= 100 ? $"Battery {power.BatteryLifePercent}%" : "Battery";
        if ((power.BatteryFlag & 8) != 0) return $"{percent}, charging";
        if (power.ACLineStatus == 1) return $"{percent}, plugged in";
        return power.BatteryLifeTime > 0 ? $"{percent}, about {Duration(TimeSpan.FromSeconds(power.BatteryLifeTime))} left" : $"{percent}, on battery";
    }

    /// <summary>"3 days 4 hours", "5 hours 12 minutes", "12 minutes".</summary>
    internal static string Duration(TimeSpan t)
    {
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (t.TotalDays >= 1) return $"{Unit((int)t.TotalDays, "day")} {Unit(t.Hours, "hour")}";
        if (t.TotalHours >= 1) return $"{Unit(t.Hours, "hour")} {Unit(t.Minutes, "minute")}";
        return Unit(Math.Max(0, t.Minutes), "minute");
    }
}
