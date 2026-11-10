using System.Globalization;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Timers;

public sealed class TimersSettings
{
    [Setting("Speak when due", Help = "Say timers, alarms and reminders out loud on the puck, as well as showing a banner and chiming.")]
    public bool Speak { get; set; } = true;

    [Setting("Late window (minutes)", Help = "After the hub was off, anything due less than this long ago still goes off; older one-off timers and reminders are dropped.", Advanced = true)]
    public int LateWindowMinutes { get; set; } = 10;
}

/// <summary>
/// Countdown timers, alarms (one-off or repeating) and reminders. Everything is kept in
/// <c>timers.json</c> so it survives restarts; one background loop fires things when they're due.
/// </summary>
public sealed class TimersPlugin : PuckPlugin<TimersSettings>, IPuckTimerHandler
{
    static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(10);
    static readonly TimeSpan MaxTimer = TimeSpan.FromHours(24);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly object _gate = new(); // guards _items, _nextId, _loaded
    List<TimerItem> _items = [];
    int _nextId = 1;
    bool _loaded;
    CancellationTokenSource? _stop;
    Task? _loop;

    public override string Id => "timers";
    public override string Name => "Timers & alarms";
    public override string Description => "Kitchen timers, alarms (one-off or repeating) and spoken reminders. Timers also show on the puck's Timer face.";
    public override string Icon => "timer";
    public override bool EnabledByDefault => true;

    string StatePath => Path.Combine(Context.DataDirectory, "timers.json");
    TimeZoneInfo Zone => Context.TimeZone;

    // ------------------------------------------------------------------ lifecycle

    public override Task StartAsync(CancellationToken cancel)
    {
        List<TimerItem> late, timers;
        lock (_gate)
        {
            EnsureLoaded();
            late = CatchUp(Context.Now);
            Save();
            timers = OfKind(TimerKind.Timer).Select(t => t.Clone()).ToList();
        }
        foreach (var t in timers) Context.ShowTimer(t.Id, t.DueAt, t.DisplayLabel, t.PuckId);
        foreach (var item in late) Announce(item);

        if (_stop is not null) return Task.CompletedTask; // already running
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(() => RunSchedulerAsync(token), CancellationToken.None);
        UpdateStatus();
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        var (stop, loop) = (_stop, _loop);
        (_stop, _loop) = (null, null);
        if (stop is null) return;
        stop.Cancel();
        if (loop is not null) await loop.ConfigureAwait(false);
        stop.Dispose();
    }

    // ------------------------------------------------------------------ timers

    [Tool("timer_set", "Start a countdown timer; it shows on the puck and rings when done. Give minutes and/or seconds, and optionally a label (\"pasta\").")]
    public ToolResult TimerSet(
        ToolCaller caller,
        [Param("Minutes, e.g. 10 or 1.5")] double? minutes = null,
        [Param("Seconds, e.g. 30")] int? seconds = null,
        [Param("What it's for, e.g. \"pasta\"")] string? label = null)
    {
        double total = (minutes ?? 0) * 60 + (seconds ?? 0);
        if (!double.IsFinite(total) || total < 1) throw new PluginException("How long should the timer be? Give minutes or seconds.");
        var duration = TimeSpan.FromSeconds(Math.Round(total));
        if (duration > MaxTimer) throw new PluginException("Timers can be up to 24 hours. For longer, set an alarm or a reminder.");

        var timer = AddTimer(duration, label, caller.PuckId);
        var name = timer.Label is null ? "Timer" : $"{TextUtil.Capitalize(timer.Label)} timer";
        return ToolResult.Ok(
            $"{name} set for {TimeMath.Short(duration)}; it ends at {TimeMath.Clock(timer.DueAt, Zone)}.",
            TextUtil.Truncate($"{TextUtil.Capitalize(timer.Label ?? "Timer")} {TimeMath.Short(duration)}", 24));
    }

    [Tool("timer_list", "Running timers with the time left on each.")]
    public string TimerList()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = Context.Now;
            var timers = OfKind(TimerKind.Timer).OrderBy(t => t.DueAt).ToList();
            if (timers.Count == 0) return "No timers running.";
            return $"{TextUtil.Plural(timers.Count, "timer")}: " +
                   string.Join("; ", timers.Select(t => $"{t.TimerName}, {TimeMath.Short(t.DueAt - now)} left ({t.Id})")) + ".";
        }
    }

    [Tool("timer_cancel", "Cancel a timer by its label or id, or \"all\". With no 'which', cancels the only timer (or the most recently set one).")]
    public ToolResult TimerCancel([Param("Label (\"pasta\"), id (\"t3\") or \"all\"")] string? which = null)
    {
        List<TimerItem> removed;
        lock (_gate)
        {
            EnsureLoaded();
            var timers = OfKind(TimerKind.Timer).ToList();
            if (timers.Count == 0) return "There are no timers running.";
            removed = IsAll(which) ? timers : [FindTimer(which, timers)];
            foreach (var t in removed) _items.Remove(t);
            Save();
        }
        foreach (var t in removed) Context.ShowTimer(t.Id, null, t.DisplayLabel, t.PuckId);
        UpdateStatus();
        return removed.Count == 1
            ? ToolResult.Ok($"Cancelled the {removed[0].TimerName}.", "Timer cancelled")
            : ToolResult.Ok($"Cancelled all {removed.Count} timers.", "Timers cancelled");
    }

    [Tool("timer_add_time", "Add minutes to a running timer (a negative number takes time off). With no 'which', uses the only (or most recently set) timer.")]
    public ToolResult TimerAddTime(
        [Param("Minutes to add, e.g. 2 (or -1 to take a minute off)")] double minutes,
        [Param("Label or id of the timer")] string? which = null)
    {
        if (!double.IsFinite(minutes) || Math.Abs(minutes) < 1 / 60.0) throw new PluginException("How many minutes should I add?");
        var change = TimeSpan.FromSeconds(Math.Round(minutes * 60));
        TimerItem timer;
        string name;
        TimeSpan left;
        lock (_gate)
        {
            EnsureLoaded();
            var timers = OfKind(TimerKind.Timer).ToList();
            if (timers.Count == 0) throw new PluginException("There are no timers running.");
            var found = FindTimer(which, timers);
            name = found.TimerName;
            var now = Context.Now;
            left = found.DueAt + change - now;
            if (left < TimeSpan.FromSeconds(1)) throw new PluginException($"That would end the {name}. Cancel it instead?");
            if (left > MaxTimer) throw new PluginException("Timers can be up to 24 hours.");
            found.DueAt += change;
            found.Duration = (found.Duration ?? TimeSpan.Zero) + change;
            Save();
            timer = found.Clone();
        }
        Context.ShowTimer(timer.Id, timer.DueAt, timer.DisplayLabel, timer.PuckId);
        var amount = TimeMath.Short(change.Duration());
        var did = change > TimeSpan.Zero ? $"added {amount}" : $"took off {amount}";
        return ToolResult.Ok(
            $"{TextUtil.Capitalize(name)}: {did}, {TimeMath.Short(left)} left.",
            TextUtil.Truncate($"{TextUtil.Capitalize(timer.Label ?? "Timer")} {(change > TimeSpan.Zero ? "+" : "-")}{amount}", 24));
    }

    // ------------------------------------------------------------------ alarms

    [Tool("alarm_set", "Set an alarm at a local time (24-hour \"HH:MM\"). Without days it rings once, at the next HH:MM; days make it repeat, e.g. [\"mon\",\"wed\"], \"weekdays\", \"weekends\" or \"daily\".")]
    public ToolResult AlarmSet(
        [Param("Time, e.g. \"07:30\"")] string time,
        ToolCaller caller,
        [Param("Optional name, e.g. \"wake up\"")] string? label = null,
        [Param("Days to repeat on, e.g. [\"mon\",\"wed\"], \"weekdays\", \"weekends\" or \"daily\". Omit for a one-off alarm.")] string[]? days = null)
    {
        if (!TimeMath.TryParseTimeOfDay(time, out var at))
            throw new PluginException($"I didn't understand the time '{time}'. Use HH:MM, e.g. 07:30.");
        if (!TimeMath.TryParseDays(days, out var repeat, out var unknown))
            throw new PluginException($"I didn't understand the day '{unknown}'. Use mon to sun, \"weekdays\", \"weekends\" or \"daily\".");
        var hhmm = at.ToString("HH:mm", Inv);
        var name = CleanLabel(label, "alarm");

        TimerItem alarm;
        DateTimeOffset now;
        lock (_gate)
        {
            EnsureLoaded();
            now = Context.Now;
            var same = OfKind(TimerKind.Alarm).FirstOrDefault(a =>
                a.Time == hhmm && TimeMath.SameDays(a.Days, repeat) && string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase));
            if (same is not null) return ToolResult.Ok($"There's already an alarm for {Describe(same, now)}.", $"Alarm {hhmm}");
            alarm = new TimerItem
            {
                Id = $"a{_nextId++}",
                Kind = TimerKind.Alarm,
                Label = name,
                Time = hhmm,
                Days = repeat.Count > 0 ? repeat : null,
                DueAt = TimeMath.NextOccurrence(at, repeat, now, Zone),
                CreatedAt = now,
                PuckId = caller.PuckId,
            };
            _items.Add(alarm);
            Save();
            alarm = alarm.Clone();
        }
        UpdateStatus();
        var title = alarm.Label is null ? "Alarm" : $"Alarm \"{alarm.Label}\"";
        var next = TimeMath.When(alarm.DueAt, now, Zone);
        var text = alarm.Repeats
            ? $"{title} set for {hhmm} {TimeMath.DaysText(alarm.Days)}; the next one is at {next}."
            : $"{title} set for {next}.";
        return ToolResult.Ok(text, $"Alarm {hhmm}");
    }

    [Tool("alarm_list", "Alarms that are set, with when each one rings next.")]
    public string AlarmList()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = Context.Now;
            var alarms = OfKind(TimerKind.Alarm).OrderBy(a => a.DueAt).ToList();
            return alarms.Count == 0
                ? "No alarms set."
                : $"{TextUtil.Plural(alarms.Count, "alarm")}: {string.Join("; ", alarms.Select(a => Describe(a, now)))}.";
        }
    }

    [Tool("alarm_cancel", "Cancel an alarm by its time (\"07:30\"), label, id, or \"all\". 'which' can be left out when there's only one alarm.")]
    public ToolResult AlarmCancel([Param("Time (\"07:30\"), label, id (\"a1\") or \"all\"")] string? which = null)
    {
        List<TimerItem> removed;
        lock (_gate)
        {
            EnsureLoaded();
            var alarms = OfKind(TimerKind.Alarm).ToList();
            if (alarms.Count == 0) return "There are no alarms set.";
            removed = IsAll(which) ? alarms : [FindAlarm(which, alarms)];
            foreach (var a in removed) _items.Remove(a);
            Save();
        }
        UpdateStatus();
        return removed.Count == 1
            ? ToolResult.Ok($"Cancelled the {AlarmName(removed[0])} alarm.", "Alarm cancelled")
            : ToolResult.Ok($"Cancelled all {removed.Count} alarms.", "Alarms cancelled");
    }

    // ------------------------------------------------------------------ reminders

    [Tool("reminder_set", "Remind the household of something later: at that time the puck chimes, shows it and says \"Reminder: <text>\". Give 'at' (\"HH:MM\", or a local date-time like \"2026-11-10T09:00\") or 'in_minutes'.")]
    public ToolResult ReminderSet(
        [Param("What to remind about, e.g. \"call Mum\"")] string text,
        ToolCaller caller,
        [Param("When: \"HH:MM\" (24-hour; the next one), or a local date-time like \"2026-11-10T09:00\"")] string? at = null,
        [Param("Or: how many minutes from now")] double? inMinutes = null)
    {
        var what = CleanReminder(text) ?? throw new PluginException("What should I remind you about?");
        var now = Context.Now;
        var due = TimeMath.ResolveWhen(at, inMinutes, now, Zone);
        TimerItem reminder;
        lock (_gate)
        {
            EnsureLoaded();
            reminder = new TimerItem { Id = $"r{_nextId++}", Kind = TimerKind.Reminder, Label = what, DueAt = due, CreatedAt = now, PuckId = caller.PuckId };
            _items.Add(reminder);
            Save();
            reminder = reminder.Clone();
        }
        UpdateStatus();
        return ToolResult.Ok(
            $"I'll remind you at {TimeMath.When(due, now, Zone)} (in {TimeMath.Short(due - now, seconds: false)}): {TextUtil.Sentence(what)}",
            $"Reminder {TimeMath.Clock(due, Zone)}");
    }

    [Tool("reminder_list", "Reminders that are set, soonest first.")]
    public string ReminderList()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = Context.Now;
            var reminders = OfKind(TimerKind.Reminder).OrderBy(r => r.DueAt).ToList();
            return reminders.Count == 0
                ? "No reminders set."
                : $"{TextUtil.Plural(reminders.Count, "reminder")}: " +
                  string.Join("; ", reminders.Select(r => $"{r.Label} at {TimeMath.When(r.DueAt, now, Zone)} ({r.Id})")) + ".";
        }
    }

    [Tool("reminder_cancel", "Cancel a reminder by a few words of it (\"Mum\"), its time, its id, or \"all\". 'which' can be left out when there's only one.")]
    public ToolResult ReminderCancel([Param("Words from the reminder, its time (\"17:30\"), id (\"r2\") or \"all\"")] string? which = null)
    {
        List<TimerItem> removed;
        lock (_gate)
        {
            EnsureLoaded();
            var reminders = OfKind(TimerKind.Reminder).OrderBy(r => r.CreatedAt).ToList();
            if (reminders.Count == 0) return "There are no reminders set.";
            removed = IsAll(which) ? reminders : [FindReminder(which, reminders)];
            foreach (var r in removed) _items.Remove(r);
            Save();
        }
        UpdateStatus();
        return removed.Count == 1
            ? ToolResult.Ok($"Cancelled the reminder: {TextUtil.Sentence(removed[0].Label ?? "")}", "Reminder cancelled")
            : ToolResult.Ok($"Cancelled all {removed.Count} reminders.", "Reminders cancelled");
    }

    // ------------------------------------------------------------------ the puck's Timer face

    public Task StartFromPuckAsync(TimeSpan duration, string? label, ToolCaller caller, CancellationToken cancel)
    {
        if (duration >= TimeSpan.FromSeconds(1) && duration <= MaxTimer) AddTimer(duration, label, caller.PuckId);
        return Task.CompletedTask;
    }

    public Task CancelFromPuckAsync(string timerId, CancellationToken cancel)
    {
        TimerItem? removed;
        lock (_gate)
        {
            EnsureLoaded();
            removed = _items.FirstOrDefault(i => i.Kind == TimerKind.Timer && i.Id == timerId);
            if (removed is not null)
            {
                _items.Remove(removed);
                Save();
            }
        }
        Context.ShowTimer(timerId, null, removed?.DisplayLabel, removed?.PuckId);
        if (removed is not null) UpdateStatus();
        return Task.CompletedTask;
    }

    public IReadOnlyList<PuckTimerInfo> ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return OfKind(TimerKind.Timer).OrderBy(t => t.DueAt)
                    .Select(t => new PuckTimerInfo(t.Id, t.DueAt, t.DisplayLabel, t.PuckId)).ToList();
            }
        }
    }

    // ------------------------------------------------------------------ context & status

    /// <summary>"Timers: pasta 4 min left. Alarm: 07:30 weekdays. Reminder: call Mum at 17:30 today." — or null.</summary>
    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_items.Count == 0) return Task.FromResult<string?>(null);
            var now = Context.Now;
            var parts = new List<string>(3);
            AddPart("Timer", OfKind(TimerKind.Timer).OrderBy(t => t.DueAt)
                .Select(t => $"{t.Label ?? t.TimerName} {TimeMath.Short(t.DueAt - now, seconds: false)} left"), ", ");
            AddPart("Alarm", OfKind(TimerKind.Alarm).OrderBy(a => a.DueAt)
                .Select(a => a.Repeats ? $"{a.Time} {TimeMath.DaysText(a.Days)}" : TimeMath.When(a.DueAt, now, Zone)), ", ");
            AddPart("Reminder", OfKind(TimerKind.Reminder).OrderBy(r => r.DueAt)
                .Select(r => $"{r.Label} at {TimeMath.When(r.DueAt, now, Zone)}"), "; ");
            return Task.FromResult<string?>(TextUtil.Truncate(string.Join(" ", parts), 400));

            void AddPart(string noun, IEnumerable<string> entries, string separator)
            {
                var list = entries.ToList();
                if (list.Count > 0) parts.Add($"{noun}{(list.Count == 1 ? "" : "s")}: {string.Join(separator, list)}.");
            }
        }
    }

    void UpdateStatus()
    {
        int timers, alarms, reminders;
        lock (_gate)
        {
            timers = OfKind(TimerKind.Timer).Count();
            alarms = OfKind(TimerKind.Alarm).Count();
            reminders = OfKind(TimerKind.Reminder).Count();
        }
        var parts = new List<string>(3);
        if (timers > 0) parts.Add(TextUtil.Plural(timers, "timer"));
        if (alarms > 0) parts.Add(TextUtil.Plural(alarms, "alarm"));
        if (reminders > 0) parts.Add(TextUtil.Plural(reminders, "reminder"));
        SetStatus(StatusLevel.Ok, parts.Count == 0 ? "Ready" : string.Join(", ", parts));
    }

    // ------------------------------------------------------------------ scheduling

    async Task RunSchedulerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var wait = MaxWait;
            try
            {
                List<TimerItem> due;
                lock (_gate)
                {
                    var now = Context.Now;
                    due = TakeDue(now);
                    if (due.Count > 0) Save();
                    if (_items.Count > 0)
                    {
                        var untilNext = _items.Min(i => i.DueAt) - now;
                        if (untilNext < wait) wait = untilNext < MinWait ? MinWait : untilNext;
                    }
                }
                if (due.Count > 0)
                {
                    UpdateStatus();
                    foreach (var item in due) Announce(item);
                }
            }
            catch (Exception e)
            {
                Log.LogError(e, "The timer scheduler hit a problem.");
            }
            try { await Task.Delay(wait, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Removes what's due (repeating alarms move to their next time) and returns copies to announce. Under the lock.</summary>
    List<TimerItem> TakeDue(DateTimeOffset now)
    {
        if (!_items.Exists(i => i.DueAt <= now)) return [];
        var due = new List<TimerItem>();
        foreach (var item in _items.Where(i => i.DueAt <= now).ToList())
        {
            due.Add(item.Clone());
            if (item.Repeats && TimeMath.TryParseTimeOfDay(item.Time, out var time))
                item.DueAt = TimeMath.NextOccurrence(time, item.Days, now, Zone);
            else
                _items.Remove(item);
        }
        return due;
    }

    /// <summary>
    /// After a (re)start: anything overdue by less than the late window goes off now; older one-offs are
    /// dropped; alarms are re-derived from their wall-clock time (which also follows a changed time zone).
    /// </summary>
    List<TimerItem> CatchUp(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Clamp(Settings.LateWindowMinutes, 0, 24 * 60));
        var fire = new List<TimerItem>();
        foreach (var item in _items.ToList())
        {
            bool overdue = item.DueAt <= now;
            if (overdue && now - item.DueAt < window) fire.Add(item.Clone());

            TimeOnly time = default;
            bool wallClock = item.Kind == TimerKind.Alarm && TimeMath.TryParseTimeOfDay(item.Time, out time);
            if (wallClock && (item.Repeats || !overdue))
                item.DueAt = TimeMath.NextOccurrence(time, item.Days, now, Zone);
            else if (overdue)
            {
                _items.Remove(item);
                if (now - item.DueAt >= window)
                    Log.LogInformation("Dropped {Kind} {Id} ({Label}), due at {Due:u}, because the hub wasn't running then.", item.Kind, item.Id, item.Label, item.DueAt);
            }
        }
        return fire;
    }

    /// <summary>Banner + chime, then speech, on the puck that set it. Runs in the background so the scheduler never waits on a puck.</summary>
    void Announce(TimerItem item)
    {
        var (banner, speech) = DueMessages(item);
        bool speak = Settings.Speak;
        _ = Task.Run(async () =>
        {
            try
            {
                if (item.Kind == TimerKind.Timer) Context.ShowTimer(item.Id, null, item.DisplayLabel, item.PuckId);
                await Context.NotifyAsync(banner, NotifyKind.Alert, chime: true, puckId: item.PuckId).ConfigureAwait(false);
                if (speak) await Context.SayAsync(speech, item.PuckId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "Couldn't announce {Kind} {Id}.", item.Kind, item.Id);
            }
        });
    }

    static (string Banner, string Speech) DueMessages(TimerItem item)
    {
        switch (item.Kind)
        {
            case TimerKind.Timer:
                return item.Label is { } label
                    ? ($"{TextUtil.Capitalize(label)} timer done", $"Your {label} timer is done.")
                    : ($"{item.TimerName} done", $"Your {TimeMath.Words(item.Duration ?? TimeSpan.Zero)} timer is done.");
            case TimerKind.Alarm:
                var spoken = TimeMath.TryParseTimeOfDay(item.Time, out var t) ? TimeMath.SpokenClock(t) : item.Time;
                return item.Label is { } name
                    ? ($"Alarm {item.Time}: {name}", $"It's {spoken} — your alarm: {TextUtil.Sentence(name)}")
                    : ($"Alarm {item.Time}", $"It's {spoken} — your alarm.");
            default:
                var text = item.Label ?? "";
                return ($"Reminder: {text}", $"Reminder: {TextUtil.Sentence(text)}");
        }
    }

    // ------------------------------------------------------------------ finding things

    TimerItem AddTimer(TimeSpan duration, string? label, string? puckId)
    {
        TimerItem timer;
        lock (_gate)
        {
            EnsureLoaded();
            var now = Context.Now;
            timer = new TimerItem
            {
                Id = $"t{_nextId++}",
                Kind = TimerKind.Timer,
                Label = CleanLabel(label, "timer"),
                DueAt = now + duration,
                Duration = duration,
                CreatedAt = now,
                PuckId = puckId,
            };
            _items.Add(timer);
            Save();
            timer = timer.Clone();
        }
        Context.ShowTimer(timer.Id, timer.DueAt, timer.DisplayLabel, timer.PuckId);
        UpdateStatus();
        return timer;
    }

    /// <summary>By id, label, or length ("the 10 minute one"); no 'which' = the only or most recent timer.</summary>
    static TimerItem FindTimer(string? which, List<TimerItem> timers)
    {
        var cleaned = string.IsNullOrWhiteSpace(which) ? null : CleanLabel(which, "timer");
        if (cleaned is null) return timers.MaxBy(t => t.CreatedAt)!;
        var byId = timers.FirstOrDefault(t => t.Id.Equals(cleaned, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var byLabel = NameMatcher.Best(cleaned, timers.Where(t => t.Label is not null), t => t.Label!);
        if (byLabel is not null) return byLabel;
        if (TimeMath.TryParseDuration(cleaned.Replace(" one", ""), out var length) &&
            timers.Where(t => t.Duration == length).MaxBy(t => t.CreatedAt) is { } byLength)
            return byLength;
        throw new PluginException($"I can't find a '{which!.Trim()}' timer. Running: {string.Join(", ", timers.Select(t => $"{t.TimerName} ({t.Id})"))}.");
    }

    TimerItem FindAlarm(string? which, List<TimerItem> alarms)
    {
        var now = Context.Now;
        var cleaned = string.IsNullOrWhiteSpace(which) ? null : CleanLabel(which, "alarm");
        if (cleaned is null)
            return alarms.Count == 1 ? alarms[0] : throw new PluginException($"Which alarm? {string.Join("; ", alarms.Select(a => Describe(a, now)))}.");
        var byId = alarms.FirstOrDefault(a => a.Id.Equals(cleaned, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        if (TimeMath.TryParseTimeOfDay(cleaned, out var t) &&
            alarms.Where(a => a.Time == t.ToString("HH:mm", Inv)).MaxBy(a => a.CreatedAt) is { } byTime)
            return byTime;
        var byLabel = NameMatcher.Best(cleaned, alarms.Where(a => a.Label is not null), a => a.Label!);
        if (byLabel is not null) return byLabel;
        if (TimeMath.TryParseDays([cleaned], out var days, out _) && days.Count > 0 &&
            alarms.FirstOrDefault(a => TimeMath.SameDays(a.Days, days)) is { } byDays)
            return byDays;
        throw new PluginException($"I can't find an alarm matching '{which!.Trim()}'. Alarms: {string.Join("; ", alarms.Select(a => Describe(a, now)))}.");
    }

    TimerItem FindReminder(string? which, List<TimerItem> reminders)
    {
        var now = Context.Now;
        string List() => string.Join("; ", reminders.Select(r => $"{r.Label} at {TimeMath.When(r.DueAt, now, Zone)} ({r.Id})"));
        if (string.IsNullOrWhiteSpace(which))
            return reminders.Count == 1 ? reminders[0] : throw new PluginException($"Which reminder? {List()}.");
        var w = which.Trim();
        var byId = reminders.FirstOrDefault(r => r.Id.Equals(w, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        if (TimeMath.TryParseTimeOfDay(w, out var t) &&
            reminders.FirstOrDefault(r => TimeMath.Clock(r.DueAt, Zone) == t.ToString("HH:mm", Inv)) is { } byTime)
            return byTime;
        return TextMatch.Best(w, reminders, r => r.Label ?? "")
               ?? throw new PluginException($"I can't find a reminder about '{w}'. Reminders: {List()}.");
    }

    /// <summary>"07:30 weekdays \"wake up\" (next 07:30 tomorrow, a1)" or "07:30 tomorrow (a2)".</summary>
    string Describe(TimerItem alarm, DateTimeOffset now)
    {
        var label = alarm.Label is null ? "" : $" \"{alarm.Label}\"";
        return alarm.Repeats
            ? $"{alarm.Time} {TimeMath.DaysText(alarm.Days)}{label} (next {TimeMath.When(alarm.DueAt, now, Zone)}, {alarm.Id})"
            : $"{TimeMath.When(alarm.DueAt, now, Zone)}{label} ({alarm.Id})";
    }

    /// <summary>"07:30 weekdays", "07:30 \"wake up\"" — for "Cancelled the … alarm".</summary>
    static string AlarmName(TimerItem alarm) =>
        alarm.Time + (alarm.Repeats ? $" {TimeMath.DaysText(alarm.Days)}" : "") + (alarm.Label is null ? "" : $" \"{alarm.Label}\"");

    IEnumerable<TimerItem> OfKind(TimerKind kind) => _items.Where(i => i.Kind == kind);

    static bool IsAll(string? which) =>
        NameMatcher.Normalize(which ?? "") is "all" or "everything" or "all of them" or "both" or "every one"
            or "all timers" or "all alarms" or "all reminders" or "all the timers" or "all the alarms" or "all the reminders";

    /// <summary>"the pasta timer" → "pasta"; null when nothing's left.</summary>
    static string? CleanLabel(string? label, string kindWord)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var words = label.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && words[0].ToLowerInvariant() is "the" or "my" or "a" or "an" or "our") words.RemoveAt(0);
        while (words.Count > 0 && words[^1].Trim('.', '!', ',').ToLowerInvariant() is var last && (last == kindWord || last == kindWord + "s"))
            words.RemoveAt(words.Count - 1);
        var cleaned = string.Join(' ', words).Trim();
        return cleaned.Length == 0 ? null : TextUtil.Truncate(cleaned, 60);
    }

    /// <summary>"to call Mum" → "call Mum".</summary>
    static string? CleanReminder(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("to ", StringComparison.OrdinalIgnoreCase) && s.Length > 3) s = s[3..].TrimStart();
        return s.Length == 0 ? null : TextUtil.Truncate(s, 300);
    }

    // ------------------------------------------------------------------ persistence (under the lock)

    void EnsureLoaded()
    {
        if (_loaded) return;
        var state = JsonFile.Load<TimerState>(StatePath, Log);
        if (state is not null)
        {
            _items = state.Items.Where(i => !string.IsNullOrEmpty(i.Id) && Enum.IsDefined(i.Kind)).ToList();
            int highest = _items.Select(i => int.TryParse(i.Id.AsSpan(1), NumberStyles.None, Inv, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            _nextId = Math.Max(state.NextId, highest + 1);
        }
        _loaded = true;
    }

    void Save()
    {
        try
        {
            JsonFile.Save(StatePath, new TimerState { NextId = _nextId, Items = _items });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep running from memory; the next change tries again.
            Log.LogWarning(e, "Couldn't save timers to {Path}.", StatePath);
        }
    }
}
