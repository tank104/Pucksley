using System.Globalization;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Weather;

/// <summary>Turns Open-Meteo data into short text that reads well aloud.</summary>
internal static class WeatherFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly string[] Directions = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    internal sealed record UnitLabels(string Temperature, string Wind, string Rain, string RainFormat);

    public static UnitLabels Labels(WeatherUnits units) => units == WeatherUnits.Imperial
        ? new("°F", "mph", "in", "0.##")
        : new("°C", "km/h", "mm", "0.#");

    /// <summary>Where the wind comes from, in words ("northwest").</summary>
    public static string Compass(double degrees) => Directions[(int)Math.Round((((degrees % 360) + 360) % 360) / 45.0) % 8];

    /// <summary>A WMO weather code in words. <paramref name="isDay"/>: true → "sunny", false/null → "clear".</summary>
    public static string Conditions(int? code, bool? isDay = null) => code switch
    {
        0 => isDay == true ? "sunny" : "clear",
        1 => isDay == true ? "mostly sunny" : "mostly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 => "fog",
        48 => "freezing fog",
        51 => "light drizzle",
        53 => "drizzle",
        55 => "heavy drizzle",
        56 => "light freezing drizzle",
        57 => "freezing drizzle",
        61 => "light rain",
        63 => "rain",
        65 => "heavy rain",
        66 => "light freezing rain",
        67 => "freezing rain",
        71 => "light snow",
        73 => "snow",
        75 => "heavy snow",
        77 => "snow grains",
        80 => "light showers",
        81 => "showers",
        82 => "heavy showers",
        85 => "light snow showers",
        86 => "heavy snow showers",
        95 => "thunderstorms",
        96 => "thunderstorms with hail",
        99 => "thunderstorms with heavy hail",
        null => "unknown conditions",
        _ => $"weather code {code}",
    };

    public static string UvWord(double uv) => Math.Round(uv) switch
    {
        < 3 => "low",
        < 6 => "moderate",
        < 8 => "high",
        < 11 => "very high",
        _ => "extreme",
    };

    /// <summary>"Wellington now: 14°C (feels like 11°C), partly cloudy. Wind 25 km/h from the northwest, gusts 45. Humidity 72%. No precipitation. Today: high 17°, low 9°, rain 40% (1.2 mm)."</summary>
    public static string Now(string place, string? timeNote, ForecastResponse f, DateTime placeNow, UnitLabels u)
    {
        var c = f.Current ?? throw new PluginException("The weather service didn't send current conditions. Try again in a minute.");
        var sb = new StringBuilder();
        sb.Append(place).Append(timeNote).Append(" now: ");
        if (c.Temperature is double t)
        {
            sb.Append(Deg(t)).Append(u.Temperature);
            if (c.FeelsLike is double feels) sb.Append(" (feels like ").Append(Deg(feels)).Append(u.Temperature).Append(')');
            sb.Append(", ");
        }
        sb.Append(Conditions(c.WeatherCode, c.IsDay is int day ? day != 0 : null)).Append('.');

        if (c.WindSpeed is double wind)
        {
            if (Math.Round(wind) < 2) sb.Append(" Calm.");
            else
            {
                sb.Append(" Wind ").Append(Whole(wind)).Append(' ').Append(u.Wind);
                if (c.WindDirection is double dir) sb.Append(" from the ").Append(Compass(dir));
                if (c.WindGusts is double gust && gust - wind >= 10) sb.Append(", gusts ").Append(Whole(gust));
                sb.Append('.');
            }
        }
        if (c.Humidity is double humidity) sb.Append(" Humidity ").Append(Whole(humidity)).Append("%.");
        if (c.Precipitation is > 0) sb.Append(" Precipitation ").Append(Rain(c.Precipitation.Value, u)).Append(' ').Append(u.Rain).Append('.');
        else if (c.WeatherCode is not >= 51) sb.Append(" No precipitation.");

        if (f.Daily is { } d && DayIndex(d, DateOnly.FromDateTime(placeNow)) is var i and >= 0)
        {
            sb.Append(" Today: ");
            var parts = new List<string>();
            if (At(d.TemperatureMax, i) is double hi) parts.Add($"high {Deg(hi)}°");
            if (At(d.TemperatureMin, i) is double lo) parts.Add($"low {Deg(lo)}°");
            if (RainPart(At(d.PrecipitationProbability, i), At(d.PrecipitationSum, i), u) is string rain) parts.Add(rain);
            sb.Append(string.Join(", ", parts)).Append('.');
        }
        return sb.ToString();
    }

    /// <summary>One line per day: "Tomorrow, Tue 10 Nov: showers, 16°/9°, rain 70% (4.2 mm), UV 5 moderate, sunrise 05:57, sunset 20:22."</summary>
    public static string Forecast(string place, string? timeNote, ForecastResponse f, DateTime placeNow, int days, UnitLabels u)
    {
        var d = f.Daily;
        if (d is null || d.Time.Length == 0) throw new PluginException("The weather service didn't send a daily forecast. Try again in a minute.");
        int start = Math.Max(0, DayIndex(d, DateOnly.FromDateTime(placeNow)));
        var sb = new StringBuilder($"Forecast for {place} ({u.Temperature} high/low{timeNote}):");
        for (int i = start; i < Math.Min(d.Time.Length, start + days); i++)
        {
            if (!DateOnly.TryParseExact(d.Time[i], "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date)) continue;
            var dayName = date.ToString("ddd d MMM", Inv);
            var label = i == start ? $"Today, {dayName}" : i == start + 1 ? $"Tomorrow, {dayName}" : dayName;
            var parts = new List<string> { Conditions(At(d.WeatherCode, i), isDay: true) };
            if (At(d.TemperatureMax, i) is double hi && At(d.TemperatureMin, i) is double lo) parts.Add($"{Deg(hi)}°/{Deg(lo)}°");
            if (RainPart(At(d.PrecipitationProbability, i), At(d.PrecipitationSum, i), u) is string rain) parts.Add(rain);
            if (At(d.UvIndexMax, i) is double uv) parts.Add($"UV {Whole(uv)} {UvWord(uv)}");
            if (ClockTime(At(d.Sunrise, i)) is string rise) parts.Add($"sunrise {rise}");
            if (ClockTime(At(d.Sunset, i)) is string set) parts.Add($"sunset {set}");
            sb.Append('\n').Append(label).Append(": ").Append(string.Join(", ", parts)).Append('.');
        }
        return sb.ToString();
    }

    /// <summary>A rain summary, then one line per hour: "15:00 15°, 70%, light rain, 1.2 mm".</summary>
    public static string Hours(string place, string? timeNote, ForecastResponse f, DateTime placeNow, int hours, UnitLabels u)
    {
        var h = f.Hourly;
        if (h is null || h.Time.Length == 0) throw new PluginException("The weather service didn't send an hourly forecast. Try again in a minute.");
        var thisHour = new DateTime(placeNow.Year, placeNow.Month, placeNow.Day, placeNow.Hour, 0, 0);
        int start = -1;
        for (int i = 0; i < h.Time.Length && start < 0; i++)
            if (TryParseLocal(h.Time[i], out var t) && t >= thisHour) start = i;
        if (start < 0) throw new PluginException("The hourly forecast I have has run out. Try again in a minute.");

        var lines = new List<string>();
        double maxChance = -1, total = 0;
        string? likeliest = null;
        for (int i = start; i < Math.Min(h.Time.Length, start + hours); i++)
        {
            if (!TryParseLocal(h.Time[i], out var t)) continue;
            var clock = t.ToString("HH:mm", Inv);
            var line = new StringBuilder(clock);
            if (At(h.Temperature, i) is double temp) line.Append(' ').Append(Deg(temp)).Append('°');
            if (At(h.PrecipitationProbability, i) is double chance)
            {
                line.Append(", ").Append(Whole(chance)).Append('%');
                if (chance > maxChance) (maxChance, likeliest) = (chance, clock);
            }
            line.Append(", ").Append(Conditions(At(h.WeatherCode, i)));
            if (At(h.Precipitation, i) is double amount && amount > 0)
            {
                line.Append(", ").Append(Rain(amount, u)).Append(' ').Append(u.Rain);
                total += amount;
            }
            lines.Add(line.ToString());
        }

        string summary = maxChance < 0 ? "No rain chances available"
            : maxChance >= 50 ? $"Rain likely: {Whole(maxChance)}% around {likeliest}"
            : maxChance >= 20 ? $"Some chance of rain: up to {Whole(maxChance)}% around {likeliest}"
            : $"Little chance of rain (at most {Whole(maxChance)}%)";
        if (total > 0) summary += $", about {Rain(total, u)} {u.Rain} in all";
        return $"Next {lines.Count} hours in {place} ({u.Temperature}, chance of rain{timeNote}). {summary}.\n" + string.Join('\n', lines);
    }

    /// <summary>"rain 40% (1.2 mm)", "rain 5%", "no rain"; null when unknown.</summary>
    static string? RainPart(double? chance, double? amount, UnitLabels u)
    {
        bool wet = amount is > 0;
        if (chance is double c)
        {
            if (Math.Round(c) == 0 && !wet) return "no rain";
            return wet ? $"rain {Whole(c)}% ({Rain(amount!.Value, u)} {u.Rain})" : $"rain {Whole(c)}%";
        }
        return wet ? $"rain {Rain(amount!.Value, u)} {u.Rain}" : null;
    }

    static int DayIndex(DailyWeather d, DateOnly day) => Array.IndexOf(d.Time, day.ToString("yyyy-MM-dd", Inv));

    static bool TryParseLocal(string? iso, out DateTime time) =>
        DateTime.TryParseExact(iso, "yyyy-MM-dd'T'HH:mm", Inv, DateTimeStyles.None, out time);

    static string? ClockTime(string? iso) => TryParseLocal(iso, out var t) ? t.ToString("HH:mm", Inv) : null;

    /// <summary>A rounded temperature, never "-0".</summary>
    internal static string Deg(double value)
    {
        var r = Math.Round(value, MidpointRounding.AwayFromZero);
        return (r == 0 ? 0 : r).ToString("0", Inv);
    }

    static string Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Inv);

    /// <summary>An amount of rain; a trace that rounds to zero reads "&lt;0.1".</summary>
    static string Rain(double value, UnitLabels u)
    {
        var text = value.ToString(u.RainFormat, Inv);
        if (text != "0" || value <= 0) return text;
        return u.RainFormat == "0.##" ? "<0.01" : "<0.1";
    }

    static T? At<T>(T?[] values, int i) where T : struct => (uint)i < (uint)values.Length ? values[i] : null;

    static string? At(string?[] values, int i) => (uint)i < (uint)values.Length ? values[i] : null;
}
