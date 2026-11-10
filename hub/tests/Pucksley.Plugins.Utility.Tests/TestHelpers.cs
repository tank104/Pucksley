using System.Diagnostics;
using System.Net;
using System.Text;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

/// <summary>An HTTP handler that answers from a function and records the requests it saw.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class TestUtil
{
    /// <summary>A plugin set up the way the hub does it: Initialize, ApplySettings, StartAsync.</summary>
    public static async Task<T> StartAsync<T>(IPluginContext context, object? settings = null) where T : IPlugin, new()
    {
        var plugin = new T();
        plugin.Initialize(context);
        plugin.ApplySettings(settings ?? Activator.CreateInstance(plugin.SettingsType)!);
        await plugin.StartAsync(CancellationToken.None);
        return plugin;
    }

    /// <summary>Polls until <paramref name="condition"/> holds, or fails after <paramref name="timeout"/>.</summary>
    public static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) Assert.Fail($"Timed out after {timeout.TotalSeconds:0.#} s waiting for {what}.");
            await Task.Delay(50);
        }
    }

    public static ToolCaller Kitchen { get; } = new("puck-kitchen", "Kitchen puck", "Kitchen");

    /// <summary>A time zone with US-style DST (2nd Sunday of March 02:00 → 03:00, 1st Sunday of November 02:00 → 01:00), UTC-5 standard.</summary>
    public static TimeZoneInfo UsEastern { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "EST", "EDT",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2007, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    /// <summary>A string of awkward characters for every shell.</summary>
    public const string Nasty = "it's \"quoted\" $(whoami) `id` $HOME %PATH% !x! ^ & | < > ( ) ; \\ \\\" 'done'";
}

/// <summary>Plugin logs into the test's output (shown for failing tests).</summary>
internal sealed class TestOutputLogger(Xunit.Abstractions.ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try { output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)}{(exception is null ? "" : "\n  " + exception)}"); }
        catch (InvalidOperationException) { } // the test already finished
    }
}

/// <summary>A fact that only runs on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A fact that needs a working bash on the PATH.</summary>
public sealed class BashFactAttribute : FactAttribute
{
    static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash", ["-c", "echo ok"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0 && output.Trim() == "ok";
        }
        catch (Exception)
        {
            return false;
        }
    });

    public BashFactAttribute()
    {
        if (!Available.Value) Skip = "bash isn't available";
    }
}

/// <summary>A fact that calls the real Open-Meteo service; set PUCKSLEY_OFFLINE=1 to skip.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PUCKSLEY_OFFLINE") == "1") Skip = "offline (PUCKSLEY_OFFLINE=1)";
    }
}
