namespace Pucksley.Core.Pucks;

/// <summary>
/// A time zone as a POSIX TZ rule ("NZST-12NZDT,M9.5.0,M4.1.0/3"), which the puck's C library
/// understands: with it the puck keeps local time, daylight saving changes included, when there's
/// no hub to tell it the time.
/// </summary>
public static class PosixTz
{
    public static string From(TimeZoneInfo tz, DateTimeOffset now)
    {
        // Linux and macOS: the zone file ends with exactly this rule
        if (FromZoneFile(tz.Id) is { } fromFile) return fromFile;
        return FromRules(tz, now);
    }

    /// <summary>The footer of a TZif (v2+) file: the rule for times after its last transition.</summary>
    internal static string? FromZoneFile(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..", StringComparison.Ordinal) || !id.Contains('/')) return null;
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("TZDIR"), "/usr/share/zoneinfo", "/usr/lib/zoneinfo" })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var path = Path.Combine(dir, id);
                if (!File.Exists(path)) continue;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 8 || bytes[0] != 'T' || bytes[1] != 'Z' || bytes[2] != 'i' || bytes[3] != 'f' || bytes[4] < '2') continue;
                int end = bytes.Length - 1;
                if (bytes[end] != '\n') continue;
                int start = Array.LastIndexOf(bytes, (byte)'\n', end - 1);
                if (start < 0 || end - start < 2) continue;
                var rule = System.Text.Encoding.ASCII.GetString(bytes, start + 1, end - start - 1);
                if (rule.All(c => c >= ' ' && c <= '~')) return rule;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>From .NET's adjustment rule for the current year (Windows ids).</summary>
    internal static string FromRules(TimeZoneInfo tz, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        var rule = tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= local && r.DateEnd >= local);
        var std = tz.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        if (rule is null || rule.DaylightDelta == TimeSpan.Zero || !tz.SupportsDaylightSavingTime) return "STD" + Offset(std);
        return $"STD{Offset(std)}DST{Offset(std + rule.DaylightDelta)},{Transition(rule.DaylightTransitionStart)},{Transition(rule.DaylightTransitionEnd)}";
    }

    // POSIX offsets are hours west of UTC: the sign flips
    static string Offset(TimeSpan utc)
    {
        var west = -utc;
        var sign = west < TimeSpan.Zero ? "-" : "";
        var a = west.Duration();
        return a.Minutes == 0 ? $"{sign}{(int)a.TotalHours}" : $"{sign}{(int)a.TotalHours}:{a.Minutes:00}";
    }

    static string Transition(TimeZoneInfo.TransitionTime t)
    {
        var tod = t.TimeOfDay.TimeOfDay;
        if (tod.Milliseconds >= 999) tod = TimeSpan.FromSeconds(Math.Ceiling(tod.TotalSeconds));  // 23:59:59.999 = midnight
        var at = tod.Minutes == 0 && tod.Seconds == 0 ? $"{(int)tod.TotalHours}" : $"{(int)tod.TotalHours}:{tod.Minutes:00}:{tod.Seconds:00}";
        if (t.IsFixedDateRule)
        {
            // Jn: day of the year, 1..365, never counting 29 February
            int day = new DateTime(2001, t.Month, Math.Min(t.Day, DateTime.DaysInMonth(2001, t.Month))).DayOfYear;
            return $"J{day}/{at}";
        }
        return $"M{t.Month}.{t.Week}.{(int)t.DayOfWeek}/{at}";
    }
}
