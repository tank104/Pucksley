using System.Globalization;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Weather;

public enum WeatherUnits { Metric, Imperial }

public sealed class WeatherSettings
{
    [Setting("Place", Help = "Leave empty to use the household location from the hub's settings.", Placeholder = "Wellington, NZ", Order = 1)]
    public string Place { get; set; } = "";

    [Setting("Units", Help = "Metric: °C, km/h, mm. Imperial: °F, mph, inches.", Order = 2)]
    public WeatherUnits Units { get; set; } = WeatherUnits.Metric;

    [Setting("Latitude", Help = "Optional exact coordinates for home (instead of looking the place up).", Placeholder = "-41.29", Order = 3, Advanced = true)]
    public string Latitude { get; set; } = "";

    [Setting("Longitude", Placeholder = "174.78", Order = 4, Advanced = true)]
    public string Longitude { get; set; } = "";
}

/// <summary>Weather from Open-Meteo: now, hour by hour, and day by day, for home or anywhere.</summary>
public sealed class WeatherPlugin : PuckPlugin<WeatherSettings>
{
    OpenMeteoClient _api = null!;

    public override string Id => "weather";
    public override string Name => "Weather";
    public override string Description => "Current conditions, hourly and 7-day forecasts from Open-Meteo (no account needed), for home or any town.";
    public override string Icon => "weather";
    public override bool EnabledByDefault => true;

    public override void Initialize(IPluginContext context)
    {
        base.Initialize(context);
        _api = new OpenMeteoClient(context.Http);
    }

    public override async Task StartAsync(CancellationToken cancel)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var home = await HomeAsync(cts.Token);
            SetStatus(StatusLevel.Ok, $"Home: {home.Name}" + (Settings.Units == WeatherUnits.Imperial ? " (°F)" : ""));
        }
        catch (PluginException e)
        {
            SetStatus(StatusLevel.Warning, e.Message);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            SetStatus(StatusLevel.Warning, "Couldn't reach Open-Meteo to look up the place yet.");
        }
    }

    [Tool("weather_now", "Current weather: temperature, feels-like, conditions, wind, humidity, precipitation, plus today's high/low and rain chance. Place defaults to home.")]
    public async Task<ToolResult> Now(
        [Param("A town or city, e.g. \"Tokyo\" or \"Portland, Oregon\". Omit for home.")] string? place = null,
        CancellationToken cancel = default)
    {
        var (loc, forecast) = await LoadAsync(place, cancel);
        var labels = WeatherFormat.Labels(Settings.Units);
        var text = WeatherFormat.Now(loc.Name, TimeNote(loc, forecast), forecast, PlaceNow(forecast), labels);
        var temp = forecast.Current?.Temperature is double t ? $" {WeatherFormat.Deg(t)}{labels.Temperature}" : "";
        return ToolResult.Ok(text, Activity(loc, temp));
    }

    [Tool("weather_forecast", "Daily forecast for 1-7 days starting today: high/low, conditions, chance and amount of rain, UV, sunrise/sunset. Place defaults to home.")]
    public async Task<ToolResult> Forecast(
        [Param("A town or city. Omit for home.")] string? place = null,
        [Param("How many days, 1-7 (default 3)", Min = 1, Max = 7)] int days = 3,
        CancellationToken cancel = default)
    {
        days = Math.Clamp(days, 1, 7);
        var (loc, forecast) = await LoadAsync(place, cancel);
        var text = WeatherFormat.Forecast(loc.Name, TimeNote(loc, forecast), forecast, PlaceNow(forecast), days, WeatherFormat.Labels(Settings.Units));
        return ToolResult.Ok(text, Activity(loc, $" {days}-day"));
    }

    [Tool("weather_hours", "Hour-by-hour forecast for the next 1-24 hours (temperature, chance of rain, conditions) — for questions like \"will it rain this afternoon?\". Place defaults to home.")]
    public async Task<ToolResult> Hours(
        [Param("A town or city. Omit for home.")] string? place = null,
        [Param("How many hours ahead, 1-24 (default 12)", Min = 1, Max = 24)] int hours = 12,
        CancellationToken cancel = default)
    {
        hours = Math.Clamp(hours, 1, 24);
        var (loc, forecast) = await LoadAsync(place, cancel);
        var text = WeatherFormat.Hours(loc.Name, TimeNote(loc, forecast), forecast, PlaceNow(forecast), hours, WeatherFormat.Labels(Settings.Units));
        return ToolResult.Ok(text, Activity(loc, $" next {hours} h"));
    }

    async Task<(WeatherLocation Location, ForecastResponse Forecast)> LoadAsync(string? place, CancellationToken cancel)
    {
        var loc = await ResolveAsync(place, cancel);
        var forecast = await _api.ForecastAsync(loc.Latitude, loc.Longitude, loc.TimeZone, Settings.Units, cancel);
        return (loc, forecast);
    }

    async Task<WeatherLocation> ResolveAsync(string? place, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(place) || IsHome(place)) return await HomeAsync(cancel);
        var found = await _api.GeocodeAsync(place, cancel)
                    ?? throw new PluginException($"I couldn't find a place called '{place.Trim()}'. Try adding the country or state, e.g. \"Paris, France\".");
        return new WeatherLocation(found.Label, found.Latitude, found.Longitude, found.TimeZone ?? "auto", IsHome: false);
    }

    /// <summary>Home: the plugin's coordinates, else its place name, else the hub's location.</summary>
    async Task<WeatherLocation> HomeAsync(CancellationToken cancel)
    {
        var zone = HouseholdZone();
        var name = FirstNonBlank(Settings.Place, Context.Hub.Location) ?? "Home";
        if (TryCoordinate(Settings.Latitude, 90, out var lat) && TryCoordinate(Settings.Longitude, 180, out var lon))
            return new WeatherLocation(name, lat, lon, zone, IsHome: true);
        if (!string.IsNullOrWhiteSpace(Settings.Place))
        {
            var found = await _api.GeocodeAsync(Settings.Place, cancel)
                        ?? throw new PluginException($"I can't find '{Settings.Place.Trim()}' (the Weather plugin's place). Check the spelling on the hub's Plugins page.");
            return new WeatherLocation(Settings.Place.Trim(), found.Latitude, found.Longitude, zone, IsHome: true);
        }
        if (Context.Hub.Latitude is double hubLat && Context.Hub.Longitude is double hubLon)
            return new WeatherLocation(name, hubLat, hubLon, zone, IsHome: true);
        if (!string.IsNullOrWhiteSpace(Context.Hub.Location))
        {
            var found = await _api.GeocodeAsync(Context.Hub.Location, cancel)
                        ?? throw new PluginException($"I can't find the household location '{Context.Hub.Location}'. Check it in the hub's settings.");
            return new WeatherLocation(Context.Hub.Location.Trim(), found.Latitude, found.Longitude, zone, IsHome: true);
        }
        throw new PluginException("I don't know where home is. Set the household location in the hub's settings, or a place in the Weather plugin's settings.");
    }

    /// <summary>"home", "here", or the home place's own name ("Wellington" for "Wellington, NZ").</summary>
    bool IsHome(string place)
    {
        if (NameMatcher.Normalize(place) is "home" or "here" or "at home" or "outside" or "local" or "my house" or "the house") return true;
        foreach (var name in new[] { Settings.Place, Context.Hub.Location })
            if (!string.IsNullOrWhiteSpace(name) && NameMatcher.Score(place, name) >= 0.85) return true;
        return false;
    }

    /// <summary>The household time zone as an IANA name for Open-Meteo (so times and days are the household's).</summary>
    string HouseholdZone()
    {
        var tz = Context.TimeZone;
        if (tz.HasIanaId) return tz.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : "auto";
    }

    /// <summary>The wall-clock time where the forecast is (the household's time for home).</summary>
    DateTime PlaceNow(ForecastResponse forecast)
    {
        try { return Context.Now.ToOffset(TimeSpan.FromSeconds(forecast.UtcOffsetSeconds)).DateTime; }
        catch (ArgumentException) { return Context.Now.DateTime; }
    }

    /// <summary>Other places are reported in their own local time; say so.</summary>
    static string? TimeNote(WeatherLocation loc, ForecastResponse forecast)
    {
        if (loc.IsHome) return null;
        var offset = TimeSpan.FromSeconds(forecast.UtcOffsetSeconds);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var text = abs.Minutes == 0 ? $"{sign}{abs.Hours}" : $"{sign}{abs.Hours}:{abs.Minutes:00}";
        return $", local time UTC{text}";
    }

    static string Activity(WeatherLocation loc, string suffix)
    {
        var shortName = loc.Name.Split(',')[0].Trim();
        return TextUtil.Truncate(shortName, Math.Max(4, 24 - suffix.Length)) + suffix;
    }

    static bool TryCoordinate(string? text, double limit, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (!s.Contains('.')) s = s.Replace(',', '.'); // "174,78" from a comma-decimal locale
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Math.Abs(value) <= limit;
    }

    static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    sealed record WeatherLocation(string Name, double Latitude, double Longitude, string TimeZone, bool IsHome);
}
