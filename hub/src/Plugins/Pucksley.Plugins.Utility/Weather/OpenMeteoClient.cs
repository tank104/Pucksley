using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Weather;

/// <summary>
/// Open-Meteo forecasts and geocoding (free, no API key). One request fetches current, hourly and
/// daily data for a location; it's cached for <see cref="CacheFor"/> so the three weather tools share it.
/// </summary>
internal sealed class OpenMeteoClient(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>When Open-Meteo is unreachable, a cached forecast up to this old is still served.</summary>
    static readonly TimeSpan StaleOk = TimeSpan.FromHours(1);

    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    // "Tokyo Japan" → country code, for qualifiers people say instead of the country's name.
    static readonly Dictionary<string, string> CountryAliases = new()
    {
        ["usa"] = "US", ["us"] = "US", ["america"] = "US", ["united states"] = "US", ["united states of america"] = "US",
        ["uk"] = "GB", ["britain"] = "GB", ["great britain"] = "GB", ["england"] = "GB", ["scotland"] = "GB",
        ["wales"] = "GB", ["united kingdom"] = "GB", ["nz"] = "NZ", ["aotearoa"] = "NZ", ["uae"] = "AE",
    };

    readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, ForecastResponse Data)> _forecasts = new();
    readonly ConcurrentDictionary<string, GeoPlace?> _places = new();

    public string ForecastUrl { get; init; } = "https://api.open-meteo.com/v1/forecast";
    public string GeocodingUrl { get; init; } = "https://geocoding-api.open-meteo.com/v1/search";

    /// <summary>Current + 7 days of hourly and daily data. <paramref name="timeZone"/> is an IANA name or "auto".</summary>
    public async Task<ForecastResponse> ForecastAsync(double latitude, double longitude, string timeZone, WeatherUnits units, CancellationToken cancel)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{latitude:F3}|{longitude:F3}|{timeZone}|{units}");
        var now = _clock();
        bool cached = _forecasts.TryGetValue(key, out var hit);
        if (cached && now - hit.At < CacheFor) return hit.Data;
        try
        {
            var data = await GetJsonAsync<ForecastResponse>(BuildForecastUrl(latitude, longitude, timeZone, units), cancel).ConfigureAwait(false);
            if (_forecasts.Count > 50) _forecasts.Clear();
            _forecasts[key] = (now, data);
            return data;
        }
        catch (PluginException) when (cached && now - hit.At < StaleOk && !cancel.IsCancellationRequested)
        {
            return hit.Data; // a forecast from a few minutes ago beats an error
        }
    }

    /// <summary>The best match for a spoken place ("Tokyo", "Portland, Oregon", "Tokyo Japan"), or null.</summary>
    public async Task<GeoPlace?> GeocodeAsync(string place, CancellationToken cancel)
    {
        var key = NameMatcher.Normalize(place);
        if (key.Length == 0) return null;
        if (_places.TryGetValue(key, out var known)) return known;

        // The geocoder understands "City, Region"; it doesn't understand "City Country" or every qualifier.
        var found = (await SearchAsync(place.Trim(), 1, cancel).ConfigureAwait(false)).FirstOrDefault();
        if (found is null)
        {
            foreach (var (name, qualifier, fromComma) in Splits(place))
            {
                var candidates = await SearchAsync(name, 10, cancel).ConfigureAwait(false);
                found = candidates.FirstOrDefault(c => MatchesQualifier(c, qualifier))
                        ?? (fromComma ? candidates.FirstOrDefault() : null);
                if (found is not null) break;
            }
        }
        if (_places.Count > 200) _places.Clear();
        _places[key] = found;
        return found;
    }

    /// <summary>Ways to split a place into name + qualifier: at the first comma, else at spaces from the right.</summary>
    static IEnumerable<(string Name, string Qualifier, bool FromComma)> Splits(string place)
    {
        place = place.Trim();
        int comma = place.IndexOf(',');
        if (comma > 0)
        {
            var q = place[(comma + 1)..].Trim(' ', ',');
            if (q.Length > 0) yield return (place[..comma].Trim(), q, true);
            yield break;
        }
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int take = words.Length - 1; take >= Math.Max(1, words.Length - 3); take--)
            yield return (string.Join(' ', words[..take]), string.Join(' ', words[take..]), false);
    }

    static bool MatchesQualifier(GeoPlace p, string qualifier)
    {
        var q = NameMatcher.Normalize(qualifier);
        if (q.Length == 0) return true;
        if (CountryAliases.TryGetValue(q, out var code)) return string.Equals(p.CountryCode, code, StringComparison.OrdinalIgnoreCase);
        if (q.Length == 2 && string.Equals(p.CountryCode, q, StringComparison.OrdinalIgnoreCase)) return true;
        return Close(q, p.Country) || Close(q, p.Admin1) || Close(q, p.Admin2);

        static bool Close(string q, string? actual) => actual is not null && NameMatcher.Score(q, actual) >= 0.8;
    }

    async Task<List<GeoPlace>> SearchAsync(string name, int count, CancellationToken cancel)
    {
        var url = $"{GeocodingUrl}?name={Uri.EscapeDataString(name)}&count={count}&language=en&format=json";
        var response = await GetJsonAsync<GeoResponse>(url, cancel).ConfigureAwait(false);
        return response.Results ?? [];
    }

    internal string BuildForecastUrl(double latitude, double longitude, string timeZone, WeatherUnits units)
    {
        var lat = latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var unitParams = units == WeatherUnits.Imperial
            ? "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch"
            : "&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm";
        return $"{ForecastUrl}?latitude={lat}&longitude={lon}" +
               "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m,is_day" +
               "&hourly=temperature_2m,precipitation_probability,precipitation,weather_code" +
               "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,sunrise,sunset,uv_index_max" +
               $"&timezone={Uri.EscapeDataString(timeZone)}&forecast_days=7{unitParams}";
    }

    async Task<T> GetJsonAsync<T>(string url, CancellationToken cancel) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = TryReadReason(body);
                throw new PluginException(reason is null
                    ? $"The weather service (Open-Meteo) answered {(int)response.StatusCode}. Try again in a minute."
                    : $"The weather service said: {reason}");
            }
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new PluginException("The weather service sent an empty answer.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException("The weather service (Open-Meteo) didn't answer in time. Try again in a minute.");
        }
        catch (HttpRequestException e)
        {
            throw new PluginException($"I couldn't reach the weather service (Open-Meteo): {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new PluginException("The weather service sent something I couldn't read.", e);
        }
    }

    static string? TryReadReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// ---- Open-Meteo response shapes (only the fields we ask for) ----

internal sealed class ForecastResponse
{
    [JsonPropertyName("utc_offset_seconds")] public int UtcOffsetSeconds { get; set; }
    [JsonPropertyName("timezone")] public string? TimeZone { get; set; }
    [JsonPropertyName("current")] public CurrentWeather? Current { get; set; }
    [JsonPropertyName("hourly")] public HourlyWeather? Hourly { get; set; }
    [JsonPropertyName("daily")] public DailyWeather? Daily { get; set; }
}

internal sealed class CurrentWeather
{
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("temperature_2m")] public double? Temperature { get; set; }
    [JsonPropertyName("apparent_temperature")] public double? FeelsLike { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double? Humidity { get; set; }
    [JsonPropertyName("precipitation")] public double? Precipitation { get; set; }
    [JsonPropertyName("weather_code")] public int? WeatherCode { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double? WindSpeed { get; set; }
    [JsonPropertyName("wind_direction_10m")] public double? WindDirection { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public double? WindGusts { get; set; }
    [JsonPropertyName("is_day")] public int? IsDay { get; set; }
}

internal sealed class HourlyWeather
{
    [JsonPropertyName("time")] public string[] Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public double?[] Temperature { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public double?[] PrecipitationProbability { get; set; } = [];
    [JsonPropertyName("precipitation")] public double?[] Precipitation { get; set; } = [];
    [JsonPropertyName("weather_code")] public int?[] WeatherCode { get; set; } = [];
}

internal sealed class DailyWeather
{
    [JsonPropertyName("time")] public string[] Time { get; set; } = [];
    [JsonPropertyName("weather_code")] public int?[] WeatherCode { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")] public double?[] TemperatureMax { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")] public double?[] TemperatureMin { get; set; } = [];
    [JsonPropertyName("precipitation_probability_max")] public double?[] PrecipitationProbability { get; set; } = [];
    [JsonPropertyName("precipitation_sum")] public double?[] PrecipitationSum { get; set; } = [];
    [JsonPropertyName("sunrise")] public string?[] Sunrise { get; set; } = [];
    [JsonPropertyName("sunset")] public string?[] Sunset { get; set; } = [];
    [JsonPropertyName("uv_index_max")] public double?[] UvIndexMax { get; set; } = [];
}

internal sealed class GeoResponse
{
    [JsonPropertyName("results")] public List<GeoPlace>? Results { get; set; }
}

internal sealed class GeoPlace
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    [JsonPropertyName("admin1")] public string? Admin1 { get; set; }
    [JsonPropertyName("admin2")] public string? Admin2 { get; set; }
    [JsonPropertyName("timezone")] public string? TimeZone { get; set; }

    /// <summary>"Tokyo, Japan"; "Portland, Oregon" for countries where the state is what people say.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            bool stateFirst = CountryCode is "US" or "CA" or "AU";
            var second = stateFirst && !string.IsNullOrEmpty(Admin1) ? Admin1 : Country;
            return string.IsNullOrEmpty(second) || second == Name ? Name : $"{Name}, {second}";
        }
    }
}
