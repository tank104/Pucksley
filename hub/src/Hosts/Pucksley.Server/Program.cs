using Pucksley.Core.Hosting;

// Pucksley hub, console edition (Linux / Docker / macOS / Windows without the tray icon).
// Settings live in PUCKSLEY_DATA (default ~/.config/pucksley, /data in a container).
var plugins = DesktopHost.LoadPlugins(DesktopHost.CommonPlugins,
    (name, e) => Console.Error.WriteLine($"Plugin assembly {name} not found: {e.Message}"));
var app = HubHost.Build(args, plugins);
await app.RunAsync();
