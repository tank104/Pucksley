using Pucksley.Plugins.Timers;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tests;

public class TimeMathTests
{
    static readonly TimeZoneInfo Eastern = TestUtil.UsEastern;
    static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        TimeMath.AtLocal(new DateOnly(year, month, day), new TimeOnly(hour, minute), Eastern);

    // ------------------------------------------------------------------ next occurrence

    [Fact]
    public void A_time_already_passed_today_is_tomorrow()
    {
        var now = Local(2026, 11, 10, 8, 0); // Tuesday 08:00
        Assert.Equal(Local(2026, 11, 11, 7, 30), TimeMath.NextOccurrence(new TimeOnly(7, 30), null, now, Eastern));
    }

    [Fact]
    public void A_time_later_today_is_today()
    {
        var now = Local(2026, 11, 10, 6, 0);
        Assert.Equal(Local(2026, 11, 10, 7, 30), TimeMath.NextOccurrence(new TimeOnly(7, 30), null, now, Eastern));
    }

    [Fact]
    public void Exactly_now_counts_as_passed()
    {
        var now = Local(2026, 11, 10, 7, 30);
        Assert.Equal(Local(2026, 11, 11, 7, 30), TimeMath.NextOccurrence(new TimeOnly(7, 30), null, now, Eastern));
    }

    [Fact]
    public void Weekdays_from_friday_morning_is_monday()
    {
        var now = Local(2026, 11, 13, 8, 0); // Friday, after 07:30
        var next = TimeMath.NextOccurrence(new TimeOnly(7, 30), Weekdays, now, Eastern);
        Assert.Equal(Local(2026, 11, 16, 7, 30), next);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(next, Eastern).DayOfWeek);
    }

    [Fact]
    public void Weekdays_before_the_time_on_a_weekday_is_today()
    {
        var now = Local(2026, 11, 13, 6, 0); // Friday 06:00
        Assert.Equal(Local(2026, 11, 13, 7, 30), TimeMath.NextOccurrence(new TimeOnly(7, 30), Weekdays, now, Eastern));
    }

    [Fact]
    public void A_single_weekday_already_passed_is_next_week()
    {
        var now = Local(2026, 11, 9, 9, 0); // Monday 09:00
        Assert.Equal(Local(2026, 11, 16, 7, 30), TimeMath.NextOccurrence(new TimeOnly(7, 30), [DayOfWeek.Monday], now, Eastern));
    }

    [Fact]
    public void Spring_forward_uses_the_new_offset_for_the_next_day()
    {
        // Saturday 7 March 2026 08:00 EST. Sunday's 07:30 is after the clocks went forward: 07:30 EDT = 11:30 UTC.
        var now = new DateTimeOffset(2026, 3, 7, 8, 0, 0, TimeSpan.FromHours(-5));
        var next = TimeMath.NextOccurrence(new TimeOnly(7, 30), null, now, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 11, 30, 0, TimeSpan.Zero), next.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(-4), next.Offset);
    }

    [Fact]
    public void A_time_skipped_by_spring_forward_moves_forward_by_the_jump()
    {
        // 02:30 doesn't exist on 8 March 2026 (02:00 → 03:00): the alarm rings at 03:30 EDT.
        var now = new DateTimeOffset(2026, 3, 7, 23, 0, 0, TimeSpan.FromHours(-5));
        var next = TimeMath.NextOccurrence(new TimeOnly(2, 30), null, now, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), next);
    }

    [Fact]
    public void A_time_repeated_by_fall_back_rings_once_at_the_first()
    {
        // 01:30 happens twice on 1 November 2026 (02:00 EDT → 01:00 EST). The first is 01:30 EDT = 05:30 UTC.
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var first = TimeMath.NextOccurrence(new TimeOnly(1, 30), null, now, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first.ToUniversalTime());

        // After it rang, the next one is the following night, not the second 01:30.
        var next = TimeMath.NextOccurrence(new TimeOnly(1, 30), null, first, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), next.ToUniversalTime());
    }

    [Fact]
    public void Fall_back_keeps_the_wall_clock_time()
    {
        // Saturday 31 October 2026, 08:00 EDT: Sunday's 07:30 is EST (-5) = 12:30 UTC.
        var now = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.FromHours(-4));
        var next = TimeMath.NextOccurrence(new TimeOnly(7, 30), null, now, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero), next.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(-5), next.Offset);
    }

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("07:30", 7, 30)]
    [InlineData("7:30", 7, 30)]
    [InlineData("19:05", 19, 5)]
    [InlineData("7.30pm", 19, 30)]
    [InlineData("7 am", 7, 0)]
    [InlineData("9:15 p.m.", 21, 15)]
    [InlineData("12am", 0, 0)]
    [InlineData("12 pm", 12, 0)]
    [InlineData("0730", 7, 30)]
    [InlineData("07:30:00", 7, 30)]
    [InlineData("noon", 12, 0)]
    [InlineData("midnight", 0, 0)]
    [InlineData("24:00", 0, 0)]
    public void Parses_times_of_day(string text, int hour, int minute)
    {
        Assert.True(TimeMath.TryParseTimeOfDay(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("7:61")]
    [InlineData("13pm")]
    [InlineData("7:5")]
    [InlineData("soon")]
    [InlineData("")]
    public void Rejects_bad_times(string text) => Assert.False(TimeMath.TryParseTimeOfDay(text, out _));

    [Theory]
    [InlineData("mon|tue", "Mon, Tue")]
    [InlineData("weekdays", "weekdays")]
    [InlineData("weekends", "weekends")]
    [InlineData("sat|sun", "weekends")]
    [InlineData("daily", "daily")]
    [InlineData("every day", "daily")]
    [InlineData("mon, wed and fri", "Mon, Wed, Fri")]
    [InlineData("Mondays|thurs", "Mon, Thu")]
    [InlineData("week days", "weekdays")]
    [InlineData("", "once")]
    public void Parses_repeat_days(string input, string expected)
    {
        var parts = input.Length == 0 ? [] : input.Split('|');
        Assert.True(TimeMath.TryParseDays(parts, out var days, out _));
        Assert.Equal(expected, TimeMath.DaysText(days));
    }

    [Fact]
    public void Unknown_day_is_reported()
    {
        Assert.False(TimeMath.TryParseDays(["mon", "funday"], out _, out var unknown));
        Assert.Equal("funday", unknown);
    }

    // ------------------------------------------------------------------ reminders

    [Fact]
    public void Reminder_at_a_local_date_time()
    {
        var now = Local(2026, 11, 9, 12, 0);
        Assert.Equal(Local(2026, 11, 10, 9, 0), TimeMath.ResolveWhen("2026-11-10T09:00", null, now, Eastern));
    }

    [Fact]
    public void Reminder_at_a_date_time_with_an_offset_is_exact()
    {
        var now = Local(2026, 11, 9, 12, 0);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(13)),
            TimeMath.ResolveWhen("2026-11-10T09:00+13:00", null, now, Eastern));
    }

    [Fact]
    public void Reminder_at_a_passed_clock_time_is_tomorrow()
    {
        var now = Local(2026, 11, 9, 18, 0);
        Assert.Equal(Local(2026, 11, 10, 17, 30), TimeMath.ResolveWhen("17:30", null, now, Eastern));
    }

    [Fact]
    public void Reminder_tomorrow_at_a_time()
    {
        var now = Local(2026, 11, 9, 18, 0);
        Assert.Equal(Local(2026, 11, 10, 9, 0), TimeMath.ResolveWhen("tomorrow at 9am", null, now, Eastern));
    }

    [Fact]
    public void Reminder_in_minutes()
    {
        var now = Local(2026, 11, 9, 18, 0);
        Assert.Equal(now.AddMinutes(45), TimeMath.ResolveWhen(null, 45, now, Eastern));
    }

    [Fact]
    public void Reminder_in_the_past_or_without_a_time_is_an_error()
    {
        var now = Local(2026, 11, 9, 18, 0);
        Assert.Throws<PluginException>(() => TimeMath.ResolveWhen("2026-11-09T09:00", null, now, Eastern));
        Assert.Throws<PluginException>(() => TimeMath.ResolveWhen(null, null, now, Eastern));
        Assert.Throws<PluginException>(() => TimeMath.ResolveWhen("whenever", null, now, Eastern));
        Assert.Throws<PluginException>(() => TimeMath.ResolveWhen(null, -5, now, Eastern));
    }

    // ------------------------------------------------------------------ formatting

    [Theory]
    [InlineData(45, true, "45 s")]
    [InlineData(250, true, "4 min 10 s")]
    [InlineData(250, false, "4 min")]
    [InlineData(3900, true, "1 h 5 min")]
    [InlineData(0.4, true, "1 s")]
    [InlineData(600, true, "10 min")]
    public void Short_durations(double seconds, bool precise, string expected) =>
        Assert.Equal(expected, TimeMath.Short(TimeSpan.FromSeconds(seconds), precise));

    [Theory]
    [InlineData(600, "10 minute")]
    [InlineData(5400, "1 hour 30 minute")]
    [InlineData(45, "45 second")]
    public void Duration_words(double seconds, string expected) => Assert.Equal(expected, TimeMath.Words(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void When_says_today_tomorrow_weekday_or_date()
    {
        var now = Local(2026, 11, 9, 12, 0); // Monday
        Assert.Equal("17:30 today", TimeMath.When(Local(2026, 11, 9, 17, 30), now, Eastern));
        Assert.Equal("07:00 tomorrow", TimeMath.When(Local(2026, 11, 10, 7, 0), now, Eastern));
        Assert.Equal("09:00 Thursday", TimeMath.When(Local(2026, 11, 12, 9, 0), now, Eastern));
        Assert.Equal("09:00 Mon 23 Nov", TimeMath.When(Local(2026, 11, 23, 9, 0), now, Eastern));
    }

    [Fact]
    public void Spoken_clock_is_twelve_hour() => Assert.Equal("7:30 pm", TimeMath.SpokenClock(new TimeOnly(19, 30)));
}
