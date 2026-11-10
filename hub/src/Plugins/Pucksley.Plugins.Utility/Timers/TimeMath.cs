using System.Globalization;
using System.Text.RegularExpressions;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Timers;

/// <summary>
/// Local-time arithmetic for alarms and reminders (DST-aware), parsing what people say ("7:30pm",
/// "weekdays", "tomorrow 9:00") and formatting durations and times for speech.
/// </summary>
internal static partial class TimeMath
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    static readonly DayOfWeek[] Weekend = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>
    /// The instant a wall-clock time happens on a date in <paramref name="tz"/>. A time skipped by a DST
    /// jump moves forward by the jump (02:30 → 03:30); a time that happens twice uses the first one.
    /// </summary>
    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            // Read it with the offset in force before the jump: it lands as far after the jump as it was into the gap.
            var before = tz.GetUtcOffset(local.AddDays(-1));
            return TimeZoneInfo.ConvertTime(new DateTimeOffset(local, before), tz);
        }
        if (tz.IsAmbiguousTime(local))
            return new DateTimeOffset(local, tz.GetAmbiguousTimeOffsets(local).Max()); // larger offset = earlier instant
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>
    /// The first time after <paramref name="after"/> (strictly) that the clock in <paramref name="tz"/> shows
    /// <paramref name="time"/>, on one of <paramref name="days"/> when any are given.
    /// </summary>
    public static DateTimeOffset NextOccurrence(TimeOnly time, IReadOnlyCollection<DayOfWeek>? days, DateTimeOffset after, TimeZoneInfo tz)
    {
        var today = Today(after, tz);
        for (int i = 0; i <= 8; i++)
        {
            var date = today.AddDays(i);
            if (days is { Count: > 0 } && !days.Contains(date.DayOfWeek)) continue;
            var at = AtLocal(date, time, tz);
            if (at > after) return at;
        }
        throw new InvalidOperationException("No occurrence within eight days."); // unreachable: every weekday is tried
    }

    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);

    [GeneratedRegex(@"^(?<h>\d{1,2})(?:[:.h]?(?<m>\d{2})(?::\d{2})?)?\s*(?<ap>a\.?\s?m\.?|p\.?\s?m\.?|a|p)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeOfDayRegex();

    /// <summary>"07:30", "7:30", "19:30", "7.30pm", "7 am", "0730", "07:30:00", "noon", "midnight".</summary>
    public static bool TryParseTimeOfDay(string? text, out TimeOnly time)
    {
        time = default;
        var s = (text ?? "").Trim().ToLowerInvariant();
        switch (s)
        {
            case "": return false;
            case "noon" or "midday": time = new TimeOnly(12, 0); return true;
            case "midnight": time = new TimeOnly(0, 0); return true;
        }
        var m = TimeOfDayRegex().Match(s);
        if (!m.Success) return false;
        int hour = int.Parse(m.Groups["h"].Value, Inv);
        int minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, Inv) : 0;
        if (minute > 59) return false;
        if (m.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return false;
            hour = hour % 12 + (m.Groups["ap"].Value[0] == 'p' ? 12 : 0);
        }
        else if (hour == 24 && minute == 0) hour = 0;
        else if (hour > 23) return false;
        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>
    /// Repeat days from ["mon","tue"], "weekdays", "weekends", "daily", "mon, wed and fri"…; nothing (or
    /// "once") means no repeat. Returns false with the word it didn't understand.
    /// </summary>
    public static bool TryParseDays(IEnumerable<string>? input, out List<DayOfWeek> days, out string? unknown)
    {
        var set = new HashSet<DayOfWeek>();
        days = [];
        unknown = null;
        foreach (var raw in input ?? [])
        {
            var text = " " + NameMatcher.Normalize(raw ?? "") + " ";
            text = text.Replace(" week days ", " weekdays ").Replace(" week ends ", " weekends ").Replace(" work days ", " weekdays ")
                       .Replace(" every day ", " daily ").Replace(" all week ", " daily ").Replace(" all days ", " daily ");
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "and" or "every" or "each" or "on" or "the" or "once" or "none" or "never" or "only":
                        continue;
                    case "weekdays" or "weekday" or "workdays" or "workday":
                        set.UnionWith(Weekdays);
                        continue;
                    case "weekends" or "weekend":
                        set.UnionWith(Weekend);
                        continue;
                    case "daily" or "everyday" or "all":
                        set.UnionWith(Enum.GetValues<DayOfWeek>());
                        continue;
                }
                if (MatchDay(word) is not DayOfWeek day)
                {
                    unknown = word;
                    return false;
                }
                set.Add(day);
            }
        }
        days = set.OrderBy(MondayFirst).ToList();
        return true;
    }

    static DayOfWeek? MatchDay(string word)
    {
        var w = word.Length > 3 && word.EndsWith('s') ? word[..^1] : word; // "mondays", "tues"
        if (w.Length < 2) return null;
        foreach (var day in Enum.GetValues<DayOfWeek>())
            if (day.ToString().ToLowerInvariant().StartsWith(w, StringComparison.Ordinal)) return day;
        return null;
    }

    static int MondayFirst(DayOfWeek d) => ((int)d + 6) % 7;

    /// <summary>"daily", "weekdays", "weekends", "Mon, Wed", or "once" for no repeat.</summary>
    public static string DaysText(IReadOnlyCollection<DayOfWeek>? days)
    {
        if (days is not { Count: > 0 }) return "once";
        var set = days.ToHashSet();
        if (set.Count == 7) return "daily";
        if (set.SetEquals(Weekdays)) return "weekdays";
        if (set.SetEquals(Weekend)) return "weekends";
        return string.Join(", ", set.OrderBy(MondayFirst).Select(d => Inv.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    public static bool SameDays(IReadOnlyCollection<DayOfWeek>? a, IReadOnlyCollection<DayOfWeek>? b) =>
        (a ?? []).ToHashSet().SetEquals(b ?? []);

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetSuffixRegex();

    /// <summary>
    /// When a reminder is due. <paramref name="at"/>: "HH:MM" (the next one), "tomorrow 9:00", or an ISO
    /// date-time (household-local unless it carries an offset); otherwise <paramref name="inMinutes"/> from now.
    /// </summary>
    public static DateTimeOffset ResolveWhen(string? at, double? inMinutes, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (!string.IsNullOrWhiteSpace(at))
        {
            var s = at.Trim();
            if (TryParseTimeOfDay(s, out var timeOfDay)) return NextOccurrence(timeOfDay, null, now, tz);

            foreach (var (word, addDays) in new[] { ("today", 0), ("tonight", 0), ("tomorrow", 1) })
            {
                if (!s.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = s[word.Length..].Trim(' ', ',');
                if (rest.StartsWith("at ", StringComparison.OrdinalIgnoreCase)) rest = rest[3..];
                if (!TryParseTimeOfDay(rest, out var t)) break;
                if (word == "tonight" && t.Hour < 12) t = t.AddHours(12); // "tonight at 8"
                return Future(AtLocal(Today(now, tz).AddDays(addDays), t, tz), now, tz);
            }

            if (s.Length > 10 && OffsetSuffixRegex().IsMatch(s) &&
                DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AllowWhiteSpaces, out var exact))
                return Future(exact, now, tz);

            if (DateTime.TryParse(s, Inv, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var local) && local.Year > 1)
            {
                bool hasTime = s.Contains(':') || s.Contains('T', StringComparison.OrdinalIgnoreCase);
                var time = hasTime ? TimeOnly.FromDateTime(local) : new TimeOnly(9, 0); // a bare date: 9 am
                return Future(AtLocal(DateOnly.FromDateTime(local), time, tz), now, tz);
            }
            throw new PluginException($"I didn't understand the time '{s}'. Use HH:MM (24-hour) or a local date and time like 2026-11-10T09:00.");
        }
        if (inMinutes is double minutes)
        {
            if (!double.IsFinite(minutes) || minutes <= 0) throw new PluginException("in_minutes must be more than 0.");
            if (minutes > 60 * 24 * 366) throw new PluginException("That's more than a year away.");
            return now + TimeSpan.FromSeconds(Math.Round(minutes * 60));
        }
        throw new PluginException("When? Give 'at' (HH:MM or a date and time) or 'in_minutes'.");
    }

    static DateTimeOffset Future(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo tz) =>
        at > now ? at : throw new PluginException($"{TextUtil.Capitalize(When(at, now, tz))} has already passed.");

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)\s*(?<u>h|hr|hrs|hours?|m|mins?|minutes?|s|secs?|seconds?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    /// <summary>"10 minute", "10 min", "90 seconds", "1 hour", "10" (minutes).</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = default;
        var m = DurationRegex().Match((text ?? "").Trim());
        if (!m.Success) return false;
        var n = double.Parse(m.Groups["n"].Value, Inv);
        var unit = m.Groups["u"].Value.ToLowerInvariant();
        duration = unit.StartsWith('h') ? TimeSpan.FromHours(n) : unit.StartsWith('s') ? TimeSpan.FromSeconds(n) : TimeSpan.FromMinutes(n);
        return duration > TimeSpan.Zero;
    }

    /// <summary>"45 s", "4 min 10 s", "1 h 5 min". With <paramref name="seconds"/> false, rounds to minutes once past a minute.</summary>
    public static string Short(TimeSpan span, bool seconds = true)
    {
        long total = (long)Math.Ceiling(Math.Max(0, span.TotalSeconds)); // "0.4 s left" reads "1 s"
        if (!seconds && total >= 60) total = (long)Math.Round(total / 60.0) * 60;
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h} h");
        if (m > 0) parts.Add($"{m} min");
        if (s > 0) parts.Add($"{s} s");
        return parts.Count == 0 ? "0 s" : string.Join(' ', parts);
    }

    /// <summary>"10 minute", "1 hour 30 minute", "45 second" — as in "your 10 minute timer".</summary>
    public static string Words(TimeSpan span)
    {
        long total = (long)Math.Round(Math.Max(0, span.TotalSeconds));
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h} hour");
        if (m > 0) parts.Add($"{m} minute");
        if (s > 0) parts.Add($"{s} second");
        return parts.Count == 0 ? "0 second" : string.Join(' ', parts);
    }

    /// <summary>"17:30 today", "07:00 tomorrow", "09:00 Wednesday", "09:00 Wed 19 Nov".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(at, tz);
        var day = DateOnly.FromDateTime(local.DateTime);
        var today = Today(now, tz);
        var clock = local.ToString("HH:mm", Inv);
        if (day == today) return $"{clock} today";
        if (day == today.AddDays(1)) return $"{clock} tomorrow";
        if (day > today && day < today.AddDays(7)) return $"{clock} {local.ToString("dddd", Inv)}";
        return $"{clock} {local.ToString("ddd d MMM", Inv)}";
    }

    /// <summary>"HH:mm" in the household's time.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(at, tz).ToString("HH:mm", Inv);

    /// <summary>"07:30" → "7:30 am" for speech.</summary>
    public static string SpokenClock(TimeOnly time) => time.ToString("h:mm tt", Inv).ToLowerInvariant();
}
