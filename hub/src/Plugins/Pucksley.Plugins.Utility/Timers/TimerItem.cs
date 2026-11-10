using System.Text.Json.Serialization;

namespace Pucksley.Plugins.Timers;

internal enum TimerKind { Timer, Alarm, Reminder }

/// <summary>A timer, alarm or reminder, as kept in <c>timers.json</c>.</summary>
internal sealed class TimerItem
{
    /// <summary>"t3", "a1", "r2": short so Claude (and people) can refer to them.</summary>
    public string Id { get; set; } = "";

    public TimerKind Kind { get; set; }

    /// <summary>A timer's or alarm's label ("pasta", "wake up"), or the reminder's text ("call Mum").</summary>
    public string? Label { get; set; }

    /// <summary>When it next goes off.</summary>
    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The puck that set it: where it's announced (null = every puck).</summary>
    public string? PuckId { get; set; }

    /// <summary>Timers: the length asked for (grows with timer_add_time).</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Alarms: the household-local time, "HH:mm".</summary>
    public string? Time { get; set; }

    /// <summary>Alarms: the days it repeats on; null = rings once.</summary>
    public List<DayOfWeek>? Days { get; set; }

    [JsonIgnore]
    public bool Repeats => Kind == TimerKind.Alarm && Days is { Count: > 0 };

    /// <summary>What the puck shows for a timer: its label, or its length ("10 min").</summary>
    [JsonIgnore]
    public string DisplayLabel => Label ?? TimeMath.Short(Duration ?? TimeSpan.Zero);

    /// <summary>"pasta timer", "10 min timer".</summary>
    [JsonIgnore]
    public string TimerName => $"{DisplayLabel} timer";

    public TimerItem Clone()
    {
        var copy = (TimerItem)MemberwiseClone();
        copy.Days = Days?.ToList();
        return copy;
    }
}

/// <summary>The whole <c>timers.json</c>.</summary>
internal sealed class TimerState
{
    public int NextId { get; set; } = 1;
    public List<TimerItem> Items { get; set; } = [];
}
