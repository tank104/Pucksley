using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Pucksley.Core.Tests.Support;

/// <summary>
/// Runs before anything else in this assembly. <c>HubPaths.DataDir</c> is resolved once per process,
/// so PUCKSLEY_DATA must point at a temp folder before any test touches it: nothing here may read or
/// write the developer's real settings. Also switches off USB scanning and mDNS, and makes sure no test can
/// reach the real Claude API (no ambient credentials; Claude points at a dead local port until the
/// fake Messages API starts and takes over the variable).
/// </summary>
static class TestEnvironment
{
    public static string DataDir { get; private set; } = "";

    /// <summary>Where Claude points before the fake starts: the discard port, so nothing answers.</summary>
    public const string DeadClaudeUrl = "http://127.0.0.1:9/";

    [ModuleInitializer]
    internal static void Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "pucksley-core-tests");
        DataDir = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(DataDir);
        CleanOldRuns(root);

        Environment.SetEnvironmentVariable("PUCKSLEY_DATA", DataDir);
        Environment.SetEnvironmentVariable("PUCKSLEY_NO_USB", "1");
        Environment.SetEnvironmentVariable("PUCKSLEY_NO_MDNS", "1");
        Environment.SetEnvironmentVariable("PUCKSLEY_WWWROOT", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
        Environment.SetEnvironmentVariable("PUCKSLEY_ANTHROPIC_BASE_URL", DeadClaudeUrl);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    static void CleanOldRuns(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-6)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A fresh empty folder under this run's temp folder.</summary>
    public static string NewTempDir(string name = "t")
    {
        var dir = Path.Combine(DataDir, "scratch", $"{name}-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
