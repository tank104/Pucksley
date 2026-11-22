using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>The PC's vital signs, gathered by the operating system's half and said the same way everywhere.</summary>
internal sealed record SystemFacts
{
    public required string Machine { get; init; }

    /// <summary>"Windows 11 Pro 25H2 (build 26200.1234)", "Omarchy (Linux 6.12.1)".</summary>
    public required string OsVersion { get; init; }

    public int CpuPercent { get; init; }
    public string? Processor { get; init; }

    /// <summary>"Memory 7.2 of 31.9 GB used (23%)", or null when it couldn't be read.</summary>
    public string? Memory { get; init; }

    /// <summary>"C: 120 GB free of 476 GB", or null when it couldn't be read.</summary>
    public string? Drive { get; init; }

    public TimeSpan Uptime { get; init; }

    /// <summary>From <see cref="BatteryText"/>.</summary>
    public string Battery { get; init; } = "No battery";

    /// <summary>One short paragraph for Claude to pick from.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"{Machine}: {OsVersion}" };
        parts.Add($"CPU {CpuPercent}%{(Processor is null ? "" : $" ({Processor})")}");
        if (Memory is not null) parts.Add(Memory);
        if (Drive is not null) parts.Add(Drive);
        parts.Add($"Up {Text.Duration(Uptime)}");
        var ips = LanTools.LocalIPv4Addresses().Take(3).Select(a => a.ToString()).ToList();
        parts.Add(ips.Count == 0 ? "No network address" : $"IP {string.Join(", ", ips)}");
        parts.Add(Battery);
        return string.Join(". ", parts) + ".";
    }

    /// <summary>"Memory 7.2 of 31.9 GB used (23%)".</summary>
    public static string MemoryText(double usedGb, double totalGb, int percent) => $"Memory {usedGb:0.0} of {totalGb:0.0} GB used ({percent}%)";

    /// <summary>"C: 120 GB free of 476 GB".</summary>
    public static string DriveText(string name, double freeGb, double totalGb) => $"{name} {freeGb:0} GB free of {totalGb:0} GB";

    /// <summary>
    /// "Battery 80%, charging", "Battery 100%, plugged in", "Battery 45%, about 2 hours 10 minutes left",
    /// "Battery 45%, on battery". <paramref name="percent"/> null when the level is unknown.
    /// </summary>
    public static string BatteryText(int? percent, bool charging, bool pluggedIn, TimeSpan? remaining)
    {
        string level = percent is int p ? $"Battery {p}%" : "Battery";
        if (charging) return $"{level}, charging";
        if (pluggedIn) return $"{level}, plugged in";
        return remaining is { } left && left > TimeSpan.Zero ? $"{level}, about {Text.Duration(left)} left" : $"{level}, on battery";
    }
}
