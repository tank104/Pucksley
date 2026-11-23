using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Agent;
using Pucksley.Core.Plugins;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Voice;
using Pucksley.Core.Web;

namespace Pucksley.Core.Hosting;

/// <summary>
/// Builds and runs the hub. Hosts (the Windows tray app, the Linux desktop app, the console for
/// servers and Docker) call <see cref="Build"/> with the plugin assemblies they ship.
/// </summary>
public static class HubHost
{
    /// <summary>The port the hub actually listens on (PUCKSLEY_PORT can override the settings).</summary>
    public static int ActualPort { get; private set; }

    public static WebApplication Build(string[] args, IEnumerable<Assembly> pluginAssemblies)
    {
        var settings = new SettingsStore();
        var port = int.TryParse(Environment.GetEnvironmentVariable("PUCKSLEY_PORT"), out var p) ? p : settings.Current.Port;
        ActualPort = port;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddProvider(new FileLoggerProvider(HubPaths.LogsDir));

        var events = new HubEvents();
        builder.Logging.AddProvider(new HubEventsLoggerProvider(events));

        var services = builder.Services;
        services.AddSingleton(settings);
        services.AddSingleton(events);
        services.AddSingleton<ClipStore>();
        services.AddSingleton<HubRuntime>(sp => new HubRuntime(
            settings,
            new Lazy<PuckManager>(sp.GetRequiredService<PuckManager>),
            new Lazy<SpeechService>(sp.GetRequiredService<SpeechService>),
            sp.GetRequiredService<ClipStore>(),
            sp.GetRequiredService<ILogger<HubRuntime>>()));
        services.AddSingleton<IHubRuntime>(sp => sp.GetRequiredService<HubRuntime>());
        services.AddSingleton(sp => new PluginManager(settings, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<IHubRuntime>));
        services.AddSingleton<IChatModel, AnthropicChatModel>();
        services.AddSingleton(sp => new AgentService(settings, sp.GetRequiredService<PluginManager>(), sp.GetRequiredService<IChatModel>(),
            events, sp.GetRequiredService<ILogger<AgentService>>(), sp.GetRequiredService<IHubRuntime>));
        services.AddSingleton<SpeechService>();
        services.AddSingleton<PuckManager>();
        services.AddSingleton<SerialScanner>();
        services.AddSingleton<MdnsAdvertiser>();
        services.AddToolSharing();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseAdminAuth();
        app.UseToolSharingAuth();
        app.MapHub();
        app.MapToolSharing();
        ToolSharing.EnsureKey(settings);

        var plugins = app.Services.GetRequiredService<PluginManager>();
        plugins.Discover(pluginAssemblies);
        // Live updates for the page (it re-reads /api/status); HubEvents throttles them
        plugins.StatusChanged += _ => events.StatusChanged("plugins");
        settings.Changed += () => events.StatusChanged("settings");

        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() =>
        {
            var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hub");
            var runtime = app.Services.GetRequiredService<HubRuntime>();
            log.LogInformation("Pucksley hub {Name} is running at {Url} (data in {Dir})", settings.Current.HubName, runtime.LanUrl, HubPaths.DataDir);
            log.LogInformation("Open the hub from another computer with: {Url}?key={Key}", runtime.LanUrl, settings.Current.AdminToken);
            _ = plugins.StartAllAsync().ContinueWith(_ => WarmUpSpeech(settings, plugins, log), TaskScheduler.Default);
            // Choosing local Whisper later fetches its model straight away too (not on the first question)
            string lastStt = settings.Current.Voice.SttEngine;
            settings.Changed += () =>
            {
                var stt = settings.Current.Voice.SttEngine;
                if (stt == lastStt) return;
                lastStt = stt;
                WarmUpSpeech(settings, plugins, log);
            };
            var pucks = app.Services.GetRequiredService<PuckManager>();
            var scanner = app.Services.GetRequiredService<SerialScanner>();
            var mdns = app.Services.GetRequiredService<MdnsAdvertiser>();
            pucks.Start();
            if (Environment.GetEnvironmentVariable("PUCKSLEY_NO_USB") != "1") scanner.Start();
            // PUCKSLEY_NO_MDNS=1: don't announce this hub on the LAN (tests; a second hub on the same network)
            bool announce = Environment.GetEnvironmentVariable("PUCKSLEY_NO_MDNS") != "1";
            // A hub that doesn't take pucks (it lends its tools to the main one) stays quiet on the
            // network and ignores pucks on USB, so they all end up with the main hub
            void ApplyPuckRole()
            {
                bool accept = settings.Current.Sharing.AcceptPucks;
                scanner.Enabled = accept;
                if (accept && announce) mdns.Start(settings.Current.HubName, settings.Current.HubId, port);
                else mdns.Stop();
                if (!accept) pucks.DisconnectAll("this hub doesn't take pucks any more");
            }
            ApplyPuckRole();
            bool lastAccept = settings.Current.Sharing.AcceptPucks;
            settings.Changed += () =>
            {
                if (settings.Current.Sharing.AcceptPucks == lastAccept) return;
                lastAccept = settings.Current.Sharing.AcceptPucks;
                ApplyPuckRole();
            };
        });
        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                app.Services.GetRequiredService<MdnsAdvertiser>().Dispose();
                app.Services.GetRequiredService<SerialScanner>().DisposeAsync().AsTask().Wait(3000);
                app.Services.GetRequiredService<PuckManager>().DisposeAsync().AsTask().Wait(3000);
                plugins.DisposeAsync().AsTask().Wait(10000);
            }
            catch (Exception) { }
        });
        return app;
    }

    /// <summary>
    /// When local Whisper is the speech engine, download its model now, so the first question
    /// doesn't wait for 142 MB.
    /// </summary>
    static void WarmUpSpeech(SettingsStore settings, PluginManager plugins, ILogger log)
    {
        if (!settings.Current.Voice.SttEngine.Equals("whisper-local", StringComparison.OrdinalIgnoreCase)) return;
        var h = plugins.Find("whisper-local");
        if (h is null || !h.Enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await h.Plugin.RunActionAsync("download-model", new Dictionary<string, string>(), CancellationToken.None).ConfigureAwait(false);
                log.LogInformation("Whisper: {Message}", r.Message);
            }
            catch (Exception e)
            {
                log.LogWarning("Whisper model download failed: {Message}", e.Message);
            }
        });
    }

    /// <summary>The hub's address for this machine's browser (with the admin key, which loopback doesn't need anyway).</summary>
    public static string LocalUrl(WebApplication app)
    {
        return $"http://127.0.0.1:{ActualPort}/";
    }
}

/// <summary>A daily log file in the data folder (kept for 7 days).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly string _dir;
    readonly object _gate = new();

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "hub-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    void Write(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"hub-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel.ToString()[..4].ToUpperInvariant()} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
