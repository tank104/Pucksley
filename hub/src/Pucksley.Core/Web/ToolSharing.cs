using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Pucksley.Core.Plugins;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Web;

/// <summary>
/// Shares this hub's tools over MCP (Streamable HTTP at <c>/mcp</c>), so another hub can use them
/// through its MCP plugin: the hub on the NAS takes the pucks and runs Claude, and a hub on the PC
/// lends it this PC's controls (audio, apps, media, power). Any MCP client can connect, Claude
/// Desktop included. Off until turned on (Settings → Household → This hub); every request needs the
/// share key as <c>Authorization: Bearer …</c>.
/// </summary>
public static class ToolSharing
{
    public const string Path = "/mcp";

    static readonly ToolCaller RemoteCaller = new(null, "another hub", null);

    public static IServiceCollection AddToolSharing(this IServiceCollection services)
    {
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "pucksley-hub", Title = "Pucksley hub", Version = "1.0" };
                o.ServerInstructions = "Tools from a Pucksley hub's plugins: for example the volume, apps, media and power of the computer it runs on.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((ctx, _) => ValueTask.FromResult(ListTools(Shared(ctx.Services!))))
            .WithCallToolHandler((ctx, cancel) => CallToolAsync(ctx.Services!.GetRequiredService<PluginManager>(), Shared(ctx.Services!), ctx.Params, cancel));
        return services;
    }

    /// <summary>The share key, checked before the MCP endpoint sees anything. Off: not found.</summary>
    public static void UseToolSharingAuth(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!path.Equals(Path, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx).ConfigureAwait(false);
                return;
            }
            var sharing = ctx.RequestServices.GetRequiredService<SettingsStore>().Current.Sharing;
            if (!sharing.ShareTools || string.IsNullOrEmpty(sharing.ShareKey))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var auth = ctx.Request.Headers.Authorization.ToString();
            var presented = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
            if (!KeyMatches(presented, sharing.ShareKey))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "This hub's tools need its share key (Authorization: Bearer …)." }).ConfigureAwait(false);
                return;
            }
            await next(ctx).ConfigureAwait(false);
        });
    }

    public static void MapToolSharing(this WebApplication app) => app.MapMcp(Path);

    /// <summary>A key for sharing, made the first time it's needed (kept encrypted in settings).</summary>
    public static string EnsureKey(SettingsStore settings)
    {
        if (string.IsNullOrEmpty(settings.Current.Sharing.ShareKey)) settings.Update(s => s.Sharing.ShareKey = NewKey());
        return settings.Current.Sharing.ShareKey;
    }

    public static string NewKey() => "puck_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    static bool KeyMatches(string presented, string key) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(key));

    /// <summary>The tools this hub shares: all of them, or only the chosen plugins'.</summary>
    static IReadOnlyList<ToolDefinition> Shared(IServiceProvider services)
    {
        var tools = services.GetRequiredService<PluginManager>().Tools;
        var only = services.GetRequiredService<SettingsStore>().Current.Sharing.SharedPlugins
            .SelectMany(p => p.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return only.Count == 0 ? tools : tools.Where(t => t.PluginId is not null && only.Contains(t.PluginId)).ToList();
    }

    internal static ListToolsResult ListTools(IReadOnlyList<ToolDefinition> tools) => new()
    {
        Tools = tools.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonSerializer.SerializeToElement(t.InputSchema),
            // Tools that ask first on the puck: the hub using them asks its person before calling
            Annotations = new ToolAnnotations { DestructiveHint = t.Confirm, ReadOnlyHint = false },
        }).ToList(),
    };

    internal static async ValueTask<CallToolResult> CallToolAsync(PluginManager plugins, IReadOnlyList<ToolDefinition> shared,
        CallToolRequestParams? p, CancellationToken cancel)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.Name)) return Failed("Which tool? None was named.");
        if (!shared.Any(t => t.Name == p.Name)) return Failed($"This hub doesn't share a tool called {p.Name}.");
        var args = new JsonObject();
        if (p.Arguments is not null)
            foreach (var kv in p.Arguments) args[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        // No question on this hub's pucks: the hub that called has already asked its person (the
        // tool is marked destructive), and nobody may be standing at this one.
        var result = await plugins.ExecuteToolAsync(p.Name, args, RemoteCaller, confirm: null, cancel).ConfigureAwait(false);
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = result.Text.Length > 0 ? result.Text : result.IsError ? "That didn't work." : "Done." },
        };
        if (result.Image is { Length: > 0 } image && !string.IsNullOrEmpty(result.ImageMediaType))
            content.Add(ImageContentBlock.FromBytes(image, result.ImageMediaType));
        return new CallToolResult { Content = content, IsError = result.IsError };
    }

    static CallToolResult Failed(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };
}
