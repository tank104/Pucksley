using System.Globalization;
using Pucksley.Plugins.Shared;

namespace Pucksley.Plugins.Heating;

/// <summary>Turns heating state and history into short, speakable text.</summary>
internal static class HeatingSummary
{
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>The full status for <c>heating_status</c>.</summary>
    public static string Status(HeatingState s, DateTimeOffset now, TimeZoneInfo zone)
    {
        var parts = new List<string>();
        var setpoint = s.SetpointC is double sp ? $"set to {Text.Num(sp)} °C" : "setpoint unknown";
        parts.Add(s.SystemMode is { Length: > 0 } mode ? $"{Text.Capitalize(Humanize(mode))}, {setpoint}." : Text.Capitalize(setpoint) + ".");

        if (s.Floors.Count > 0)
        {
            var floors = s.Floors.Select(f =>
            {
                var text = $"{Floor(f.Name)} {(f.ReadingC is double r ? Text.Fixed(r) + " °C" : "no reading")}";
                if (f.CallingForHeat) text += ", heating";
                if (f.SetpointC is double t && s.SetpointC is double house && Math.Abs(t - house) >= 0.05) text += $" (target {Text.Num(t)})";
                if (f.InSafeState) text += ", in safe mode";
                if (f.Faults.Count > 0) text += $", fault: {string.Join(", ", f.Faults)}";
                return text;
            });
            parts.Add(Text.Capitalize(string.Join("; ", floors)) + ".");
        }

        if (s.HeatPump is { } hp)
        {
            if (hp.LinkHealth is { } link && !link.Equals("alive", StringComparison.OrdinalIgnoreCase))
                parts.Add($"Heat pump link is {Humanize(link)}.");
            else
            {
                var bits = new List<string>();
                if (hp.OperatingMode is { Length: > 0 } op) bits.Add($"{Humanize(op)} mode");
                if (hp.FlowTempC is double flow) bits.Add($"flow {Text.Num(flow)} °C");
                if (bits.Count > 0) parts.Add($"Heat pump {string.Join(", ", bits)}.");
            }
            if (hp.SummerMode) parts.Add("Heat pump is in summer mode.");
            if (hp.DewpointAlarm) parts.Add("Dew-point alarm!");
            if (hp.FaultBitmap != 0) parts.Add($"Heat pump fault code {hp.FaultBitmap}.");
        }

        var overrides = s.Groups.Where(g => g.Override is not null).Select(g => $"{HeatingZones.Describe(g.Name)} {KindText(g.Override!.Kind)}{Until(g.Override.ExpiresAt, now, zone)}").ToList();
        if (overrides.Count > 0) parts.Add($"Override{(overrides.Count > 1 ? "s" : "")}: {string.Join("; ", overrides)}.");

        var offsets = s.Groups.Where(g => Math.Abs(g.OffsetC) >= 0.05).Select(g => $"{HeatingZones.Describe(g.Name)} {g.OffsetC.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}").ToList();
        if (offsets.Count > 0) parts.Add($"Offsets: {string.Join(", ", offsets)}.");

        parts.Add(s.Faults.Count > 0 ? $"Faults: {string.Join(", ", s.Faults)}." : "No faults.");
        if (s.LastReportedAt is DateTimeOffset at && now - at > StaleAfter) parts.Add($"The controller last reported {Ago(now - at)} ago.");
        if (s.IsStaleConfig) parts.Add("A settings change hasn't reached the controller yet.");
        return string.Join(" ", parts);
    }

    /// <summary>One line for every turn, e.g. <c>Heating: 21.0 °C set, heating; ground 21.4, middle 20.8 (heating), top 20.0.</c></summary>
    public static string Context(HeatingState s, DateTimeOffset now)
    {
        var head = s.SetpointC is double sp ? $"{Text.Fixed(sp)} °C set" : "setpoint unknown";
        if (s.SystemMode is { Length: > 0 } mode) head += $", {Humanize(mode)}";
        var floors = s.Floors.Select(f => $"{f.Name} {(f.ReadingC is double r ? Text.Fixed(r) : "?")}{(f.CallingForHeat ? " (heating)" : "")}");
        var text = $"Heating: {head}; {string.Join(", ", floors)}.";
        if (s.Faults.Count > 0) text += $" Faults: {string.Join(", ", s.Faults)}.";
        if (s.LastReportedAt is DateTimeOffset at && now - at > StaleAfter) text += $" (Last report {Ago(now - at)} ago.)";
        return text;
    }

    /// <summary>Min / max / latest / trend for <c>heating_history</c>.</summary>
    public static string History(string floor, double hours, IReadOnlyList<HistoryRow> rows)
    {
        var name = Text.Capitalize(HeatingZones.DescribeFloor(floor));
        var span = hours == 1 ? "hour" : $"{Text.Num(hours)} hours";
        var readings = rows.Where(r => r.ReadingC is not null).ToList();
        if (readings.Count == 0) return $"No readings for the {HeatingZones.DescribeFloor(floor)} in the last {span}.";
        double min = readings.Min(r => r.ReadingC!.Value), max = readings.Max(r => r.ReadingC!.Value);
        double first = readings[0].ReadingC!.Value, latest = readings[^1].ReadingC!.Value;
        double delta = latest - first;
        var trend = Math.Abs(delta) < 0.2 ? "steady" : $"{(delta > 0 ? "up" : "down")} {Text.Num(Math.Abs(delta))} °C";
        var text = $"{name}, last {span}: {Text.Fixed(min)}–{Text.Fixed(max)} °C, now {Text.Fixed(latest)} °C, {trend}";
        int calls = rows.Count(r => string.Equals(r.Call, "heat", StringComparison.OrdinalIgnoreCase));
        if (rows.Count > 0) text += $"; calling for heat {Math.Round(100.0 * calls / rows.Count):0}% of the time";
        if (rows[^1].SetpointC is double target) text += $"; target {Text.Num(target)} °C";
        return text + ".";
    }

    static string Floor(string name) => name switch
    {
        "ground" => "ground",
        "middle" => "middle",
        "top" => "top",
        _ => name.Replace('_', ' '),
    };

    static string KindText(string kind) => kind switch
    {
        "force_open" => "forced open",
        "force_closed" => "forced closed",
        "hold_at_setpoint" => "held at setpoint",
        _ => Humanize(kind),
    };

    static string Until(DateTimeOffset? expires, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (expires is not DateTimeOffset e) return "";
        var local = TimeZoneInfo.ConvertTime(e, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        if (local.Date == today) return $" until {time}";
        if (local.Date == today.AddDays(1)) return $" until {time} tomorrow";
        return $" until {local.ToString("ddd", CultureInfo.InvariantCulture)} {time}";
    }

    static string Ago(TimeSpan t) =>
        t.TotalMinutes < 90 ? $"{Math.Max(1, (int)t.TotalMinutes)} minutes"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} hours"
        : $"{(int)t.TotalDays} days";

    static string Humanize(string s) => s.Replace('_', ' ');
}
