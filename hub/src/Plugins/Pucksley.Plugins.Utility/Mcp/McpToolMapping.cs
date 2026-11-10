using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Mcp;

/// <summary>Maps MCP tools to Puck tools: names, descriptions, input schemas, arguments and results.</summary>
internal static class McpToolMapping
{
    const int MaxServerKey = 24;
    const int MaxDescription = 2000;
    const int MaxResultText = 16_000;

    /// <summary>A short key for a server's name, unique among servers ("My GitHub" → "my_github").</summary>
    public static string ServerKey(string? name, ISet<string> used)
    {
        var slug = TextUtil.Slug(name);
        if (slug.Length == 0) slug = "server";
        if (slug.Length > MaxServerKey) slug = slug[..MaxServerKey].TrimEnd('_');
        var candidate = slug;
        for (int n = 2; !used.Add(candidate); n++) candidate = $"{slug}{n}";
        return candidate;
    }

    /// <summary><c>mcp_&lt;server&gt;_&lt;tool&gt;</c> in <c>[a-z0-9_]</c>, at most 64 characters, unique within <paramref name="used"/>.</summary>
    public static string ToolName(string serverKey, string toolName, ISet<string> used)
    {
        var tool = TextUtil.Slug(toolName);
        if (tool.Length == 0) tool = "tool";
        var name = $"mcp_{serverKey}_{tool}";
        if (name.Length > 64)
        {
            // Keep the start and add a short, stable hash of the full name so it stays unique (and the same after a restart).
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverKey}/{toolName}")))[..6].ToLowerInvariant();
            name = name[..57].TrimEnd('_') + "_" + hash;
        }
        var candidate = name;
        for (int n = 2; !used.Add(candidate); n++)
        {
            var suffix = $"_{n}";
            candidate = (name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)].TrimEnd('_') : name) + suffix;
        }
        return candidate;
    }

    /// <summary>The tool's description, prefixed with the server's name.</summary>
    public static string Description(string serverName, Tool tool)
    {
        var text = (tool.Description ?? "").Trim();
        if (text.Length == 0) text = (tool.Title ?? "").Trim();
        if (text.Length == 0) text = tool.Name;
        return TextUtil.Truncate($"{serverName}: {text}", MaxDescription);
    }

    /// <summary>The tool's input schema as an object schema (Claude requires one), without <c>$schema</c>.</summary>
    public static JsonObject InputSchema(JsonElement schema)
    {
        JsonObject result = new();
        if (schema.ValueKind == JsonValueKind.Object)
        {
            try { result = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { }
        }
        result.Remove("$schema");
        if (result["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String || type.GetValue<string>() != "object")
            result["type"] = "object";
        if (result["properties"] is not JsonObject) result["properties"] = new JsonObject();
        return result;
    }

    /// <summary>Claude's arguments for <c>tools/call</c>.</summary>
    public static Dictionary<string, JsonElement> ToArguments(JsonObject arguments)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            using var doc = JsonDocument.Parse(value?.ToJsonString() ?? "null");
            result[key] = doc.RootElement.Clone();
        }
        return result;
    }

    /// <summary>
    /// Text blocks joined; the first PNG/JPEG image becomes the result's image; other blocks are mentioned
    /// briefly; <c>isError</c> becomes an error result.
    /// </summary>
    public static ToolResult ToToolResult(CallToolResult result)
    {
        var text = new StringBuilder();
        byte[]? image = null;
        string? imageType = null;
        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock t:
                    AppendLine(text, t.Text);
                    break;
                case ImageContentBlock i when image is null && ImageType(i.MimeType) is { } type:
                    try
                    {
                        image = i.DecodedData.ToArray();
                        imageType = type;
                    }
                    catch (Exception e) when (e is FormatException or InvalidOperationException)
                    {
                        AppendLine(text, "[an image that couldn't be decoded]");
                    }
                    break;
                case ImageContentBlock i:
                    AppendLine(text, $"[image ({i.MimeType}) not shown]");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    AppendLine(text, resource.Text);
                    break;
                case EmbeddedResourceBlock embedded:
                    AppendLine(text, $"[resource {embedded.Resource?.Uri}]");
                    break;
                case ResourceLinkBlock link:
                    AppendLine(text, $"[{link.Name}: {link.Uri}]");
                    break;
                case AudioContentBlock:
                    AppendLine(text, "[audio not played]");
                    break;
            }
        }
        if (text.Length == 0 && result.StructuredContent is { } structured) text.Append(structured.GetRawText());

        var s = TextUtil.Truncate(text.ToString().Trim(), MaxResultText);
        if (result.IsError == true) return ToolResult.Error(s.Length > 0 ? s : "The tool reported an error.");
        if (s.Length == 0) s = image is null ? "Done." : "(image)";
        return new ToolResult { Text = s, Image = image, ImageMediaType = imageType };
    }

    static void AppendLine(StringBuilder sb, string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(line);
    }

    /// <summary>The hub passes PNG and JPEG images to Claude.</summary>
    static string? ImageType(string? mimeType) => mimeType?.Trim().ToLowerInvariant() switch
    {
        "image/png" => "image/png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => "image/jpeg",
        _ => null,
    };
}
