using System.Reflection;
using Pucksley.Core.Hosting;

// Pucksley hub, console edition (Linux / Docker / macOS / Windows without the tray icon).
// Settings live in PUCKSLEY_DATA (default ~/.config/pucksley, /data in a container).
var app = HubHost.Build(args, PluginAssemblies.Load());
await app.RunAsync();

static class PluginAssemblies
{
    static readonly string[] Names = ["Pucksley.Plugins.Music", "Pucksley.Plugins.Home", "Pucksley.Plugins.Utility", "Pucksley.Plugins.Speech"];

    public static IEnumerable<Assembly> Load()
    {
        foreach (var name in Names)
        {
            Assembly? a = null;
            try { a = Assembly.Load(name); }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.Error.WriteLine($"Plugin assembly {name} not found: {e.Message}");
            }
            if (a is not null) yield return a;
        }
    }
}
