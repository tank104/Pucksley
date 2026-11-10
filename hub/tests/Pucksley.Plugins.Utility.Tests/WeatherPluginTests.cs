using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Pucksley.Plugins.Weather;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Tests;

public class WeatherPluginTests(ITestOutputHelper output)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly TimeZoneInfo Auckland = FindZone("Pacific/Auckland", "New Zealand Standard Time");

    const string GeoTokyo = """{"results":[{"name":"Tokyo","latitude":35.6895,"longitude":139.69171,"country":"Japan","country_code":"JP","admin1":"Tokyo","timezone":"Asia/Tokyo"}]}""";

    static TimeZoneInfo FindZone(params string[] ids)
    {
        foreach (var id in ids)
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        throw new InvalidOperationException("No test time zone found.");
    }

    /// <summary>A fake Open-Meteo: knows Tokyo, and builds a forecast around "now" in the zone asked for.</summary>
    static StubHttpHandler FakeOpenMeteo(Func<HttpResponseMessage?>? fail = null) => new((request, _) =>
    {
        if (fail?.Invoke() is { } failure) return failure;
        var url = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(url.Query);
        if (url.Host.StartsWith("geocoding-api", StringComparison.Ordinal))
            return StubHttpHandler.Json(query["name"] == "Tokyo" ? GeoTokyo : "{}");
        var offset = query["timezone"] == "Asia/Tokyo" ? TimeSpan.FromHours(9) : Auckland.GetUtcOffset(DateTimeOffset.UtcNow);
        return StubHttpHandler.Json(Forecast(DateTimeOffset.UtcNow.ToOffset(offset)));
    });

    /// <summary>Overcast 14.5° now; light rain (70%, 1.2 mm) three hours from now; showery days, 17°/9°.</summary>
    static string Forecast(DateTimeOffset placeNow)
    {
        var today = placeNow.Date;
        int rainHour = placeNow.Hour + 3;
        var hours = Enumerable.Range(0, 48).ToList();
        var days = Enumerable.Range(0, 7).Select(d => today.AddDays(d).ToString("yyyy-MM-dd", Inv)).ToList();
        static JsonArray Arr<T>(IEnumerable<T> values) => new(values.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
        return new JsonObject
        {
            ["utc_offset_seconds"] = (int)placeNow.Offset.TotalSeconds,
            ["current"] = new JsonObject
            {
                ["time"] = placeNow.ToString("yyyy-MM-dd'T'HH:mm", Inv),
                ["temperature_2m"] = 14.5, ["apparent_temperature"] = 11.7, ["relative_humidity_2m"] = 84, ["precipitation"] = 0.0,
                ["weather_code"] = 3, ["wind_speed_10m"] = 23.2, ["wind_direction_10m"] = 315, ["wind_gusts_10m"] = 54.7, ["is_day"] = 1,
            },
            ["hourly"] = new JsonObject
            {
                ["time"] = Arr(hours.Select(h => today.AddHours(h).ToString("yyyy-MM-dd'T'HH:mm", Inv))),
                ["temperature_2m"] = Arr(hours.Select(h => 10.0 + h % 10)),
                ["precipitation_probability"] = Arr(hours.Select(h => h == rainHour ? 70 : 10)),
                ["precipitation"] = Arr(hours.Select(h => h == rainHour ? 1.2 : 0.0)),
                ["weather_code"] = Arr(hours.Select(h => h == rainHour ? 61 : 2)),
            },
            ["daily"] = new JsonObject
            {
                ["time"] = Arr(days),
                ["weather_code"] = Arr(days.Select(_ => 80)),
                ["temperature_2m_max"] = Arr(days.Select(_ => 17.4)),
                ["temperature_2m_min"] = Arr(days.Select(_ => 9.2)),
                ["precipitation_probability_max"] = Arr(days.Select(_ => 40)),
                ["precipitation_sum"] = Arr(days.Select(_ => 1.2)),
                ["sunrise"] = Arr(days.Select(d => d + "T05:58")),
                ["sunset"] = Arr(days.Select(d => d + "T20:21")),
                ["uv_index_max"] = Arr(days.Select(_ => 6.3)),
            },
        }.ToJsonString();
    }

    static FakePluginContext Context(StubHttpHandler handler) => new(http: new HttpClient(handler)) { TimeZone = Auckland };

    static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}") => FakePluginContext.CallToolAsync(plugin, tool, json);

    static List<Uri> ForecastRequests(StubHttpHandler handler) =>
        handler.Requests.Select(r => r.Request.RequestUri!).Where(u => u.Host == "api.open-meteo.com").ToList();

    [Fact]
    public async Task Now_at_home_reads_well()
    {
        var handler = FakeOpenMeteo();
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler));
        Assert.Equal((StatusLevel.Ok, "Home: Wellington, NZ"), (plugin.Status.Level, plugin.Status.Message));

        var result = await Call(plugin, "weather_now");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(
            "Wellington, NZ now: 15°C (feels like 12°C), overcast. Wind 23 km/h from the northwest, gusts 55. Humidity 84%. No precipitation. " +
            "Today: high 17°, low 9°, rain 40% (1.2 mm).", result.Text);
        Assert.Equal("Wellington 15°C", result.Activity);

        var url = Assert.Single(ForecastRequests(handler)).ToString();
        Assert.Contains("latitude=-41.29&longitude=174.78", url);
        Assert.Contains("timezone=Pacific%2FAuckland", url);
        Assert.Contains("wind_speed_unit=kmh", url);
    }

    [Fact]
    public async Task Forecast_and_hours_share_one_cached_request()
    {
        var handler = FakeOpenMeteo();
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler));
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Auckland).Date;

        var forecast = await Call(plugin, "weather_forecast", """{"days":2}""");
        var day = "light showers, 17°/9°, rain 40% (1.2 mm), UV 6 high, sunrise 05:58, sunset 20:21.";
        Assert.Equal(
            $"Forecast for Wellington, NZ (°C high/low):\nToday, {today.ToString("ddd d MMM", Inv)}: {day}\nTomorrow, {today.AddDays(1).ToString("ddd d MMM", Inv)}: {day}",
            forecast.Text);
        Assert.Equal("Wellington 2-day", forecast.Activity);

        var hours = await Call(plugin, "weather_hours", """{"hours":6}""");
        var lines = hours.Text.Split('\n');
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Auckland);
        var rainAt = local.Date.AddHours(local.Hour + 3).ToString("HH:mm", Inv);
        Assert.Equal($"Next 6 hours in Wellington, NZ (°C, chance of rain). Rain likely: 70% around {rainAt}, about 1.2 mm in all.", lines[0]);
        Assert.Equal(7, lines.Length);
        Assert.Contains(lines, l => l.StartsWith($"{rainAt} ") && l.EndsWith(", 70%, light rain, 1.2 mm"));
        Assert.Equal("Wellington next 6 h", hours.Activity);

        await Call(plugin, "weather_now");
        Assert.Single(ForecastRequests(handler)); // all three from one cached response
    }

    [Fact]
    public async Task Other_places_are_geocoded_and_in_their_local_time()
    {
        var handler = FakeOpenMeteo();
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler));

        var result = await Call(plugin, "weather_now", """{"place":"Tokyo"}""");
        Assert.StartsWith("Tokyo, Japan, local time UTC+9 now: 15°C", result.Text);
        Assert.Equal("Tokyo 15°C", result.Activity);
        var forecastUrl = ForecastRequests(handler).Single().ToString();
        Assert.Contains("latitude=35.6895&longitude=139.6917", forecastUrl);
        Assert.Contains("timezone=Asia%2FTokyo", forecastUrl);

        // "Tokyo Japan" isn't understood by the geocoder: it's retried as "Tokyo" filtered by country.
        var spoken = await Call(plugin, "weather_hours", """{"place":"Tokyo Japan","hours":2}""");
        Assert.StartsWith("Next 2 hours in Tokyo, Japan (°C, chance of rain, local time UTC+9).", spoken.Text);

        var unknown = await Call(plugin, "weather_now", """{"place":"Atlantis"}""");
        Assert.True(unknown.IsError);
        Assert.Equal("I couldn't find a place called 'Atlantis'. Try adding the country or state, e.g. \"Paris, France\".", unknown.Text);
    }

    [Fact]
    public async Task The_home_town_by_name_is_home()
    {
        var handler = FakeOpenMeteo();
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler));
        Assert.StartsWith("Wellington, NZ now:", (await Call(plugin, "weather_now", """{"place":"wellington"}""")).Text);
        Assert.StartsWith("Wellington, NZ now:", (await Call(plugin, "weather_now", """{"place":"home"}""")).Text);
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.Host.StartsWith("geocoding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_can_move_home_and_switch_to_imperial()
    {
        var handler = FakeOpenMeteo();
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler), new WeatherSettings { Place = "Tokyo", Units = WeatherUnits.Imperial });
        Assert.Equal("Home: Tokyo (°F)", plugin.Status.Message);

        var result = await Call(plugin, "weather_now");
        Assert.StartsWith("Tokyo now: 15°F (feels like 12°F), overcast. Wind 23 mph from the northwest", result.Text);
        var url = ForecastRequests(handler).Single().ToString();
        Assert.Contains("latitude=35.6895", url);
        Assert.Contains("timezone=Pacific%2FAuckland", url); // still the household's time
        Assert.Contains("temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch", url);

        var coordinates = await TestUtil.StartAsync<WeatherPlugin>(Context(handler), new WeatherSettings { Latitude = "-43.53", Longitude = "172,63" });
        await Call(coordinates, "weather_now");
        Assert.Contains("latitude=-43.53&longitude=172.63", ForecastRequests(handler).Last().ToString());
    }

    [Fact]
    public async Task Without_a_home_location_it_says_how_to_fix_it()
    {
        var ctx = Context(FakeOpenMeteo());
        ctx.Latitude = null;
        ctx.Longitude = null;
        ctx.Location = null;
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(ctx);
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
        var result = await Call(plugin, "weather_now");
        Assert.True(result.IsError);
        Assert.StartsWith("I don't know where home is.", result.Text);
        Assert.False((await Call(plugin, "weather_now", """{"place":"Tokyo"}""")).IsError); // other places still work
    }

    [Fact]
    public async Task Service_errors_are_explained()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("""{"reason":"Latitude must be in range of -90 to 90°.","error":true}""", HttpStatusCode.BadRequest));
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(Context(handler));
        var result = await Call(plugin, "weather_now");
        Assert.True(result.IsError);
        Assert.Equal("The weather service said: Latitude must be in range of -90 to 90°.", result.Text);
    }

    [Fact]
    public async Task Cache_expires_after_ten_minutes_and_covers_outages()
    {
        var clock = DateTimeOffset.UtcNow;
        bool down = false;
        var handler = FakeOpenMeteo(() => down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);
        var api = new OpenMeteoClient(new HttpClient(handler), () => clock);

        await api.ForecastAsync(-41.29, 174.78, "Pacific/Auckland", WeatherUnits.Metric, default);
        clock += TimeSpan.FromMinutes(9);
        await api.ForecastAsync(-41.29, 174.78, "Pacific/Auckland", WeatherUnits.Metric, default);
        Assert.Single(ForecastRequests(handler));

        clock += TimeSpan.FromMinutes(2);
        await api.ForecastAsync(-41.29, 174.78, "Pacific/Auckland", WeatherUnits.Metric, default);
        Assert.Equal(2, ForecastRequests(handler).Count);

        // Open-Meteo is down: a forecast from less than an hour ago is still served.
        down = true;
        clock += TimeSpan.FromMinutes(30);
        var stale = await api.ForecastAsync(-41.29, 174.78, "Pacific/Auckland", WeatherUnits.Metric, default);
        Assert.Equal(14.5, stale.Current!.Temperature);
        clock += TimeSpan.FromHours(2);
        await Assert.ThrowsAsync<PluginException>(() => api.ForecastAsync(-41.29, 174.78, "Pacific/Auckland", WeatherUnits.Metric, default));
    }

    [Theory]
    [InlineData(0, "north")]
    [InlineData(350, "north")]
    [InlineData(100, "east")]
    [InlineData(225, "southwest")]
    [InlineData(-45, "northwest")]
    public void Wind_directions_in_words(double degrees, string expected) => Assert.Equal(expected, WeatherFormat.Compass(degrees));

    [Fact]
    public void Weather_codes_and_uv_in_words()
    {
        Assert.Equal("sunny", WeatherFormat.Conditions(0, isDay: true));
        Assert.Equal("clear", WeatherFormat.Conditions(0, isDay: false));
        Assert.Equal("thunderstorms with hail", WeatherFormat.Conditions(96));
        Assert.Equal("weather code 42", WeatherFormat.Conditions(42));
        Assert.Equal("low", WeatherFormat.UvWord(2.4));
        Assert.Equal("moderate", WeatherFormat.UvWord(2.6));
        Assert.Equal("extreme", WeatherFormat.UvWord(10.6));
        Assert.Equal("0", WeatherFormat.Deg(-0.3));
    }

    [LiveFact]
    public async Task Live_open_meteo()
    {
        var plugin = await TestUtil.StartAsync<WeatherPlugin>(new FakePluginContext()); // Wellington, NZ; real HTTP
        foreach (var (tool, args) in new[]
                 {
                     ("weather_now", "{}"),
                     ("weather_forecast", """{"place":"Portland, Oregon","days":3}"""),
                     ("weather_hours", """{"place":"Tokyo","hours":4}"""),
                 })
        {
            var result = await Call(plugin, tool, args);
            output.WriteLine($"{tool} {args} → [{result.Activity}]\n{result.Text}\n");
            Assert.False(result.IsError, result.Text);
        }
        Assert.StartsWith("Wellington, NZ now: ", (await Call(plugin, "weather_now")).Text);
        Assert.Contains("Tomorrow, ", (await Call(plugin, "weather_forecast", """{"place":"Portland, Oregon","days":3}""")).Text);
        Assert.StartsWith("Next 4 hours in Tokyo, Japan", (await Call(plugin, "weather_hours", """{"place":"Tokyo","hours":4}""")).Text);
    }
}
