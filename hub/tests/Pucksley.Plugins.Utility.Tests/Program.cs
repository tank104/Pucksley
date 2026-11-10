using System.Text;

namespace Pucksley.Plugins.Tests;

/// <summary>
/// The test assembly is also a small program the tests start as a child process:
/// <c>--mcp-test-server</c> runs a stdio MCP server; <c>--echo-args</c> prints each argument on its own line
/// (to check that shell quoting delivers values intact). Test runners don't use this entry point.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--mcp-test-server")
        {
            await TestMcpServer.RunAsync();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--echo-args")
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            foreach (var arg in args.Skip(1)) stdout.WriteLine("[" + arg + "]");
            return 0;
        }
        Console.Error.WriteLine("This is a test assembly: run it with dotnet test.");
        return 1;
    }

    /// <summary>The test assembly's path, for starting it as a child process with <c>dotnet</c>.</summary>
    public static string AssemblyPath => typeof(Program).Assembly.Location;

    /// <summary>The dotnet host running the tests (or "dotnet" from the PATH).</summary>
    public static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
}
