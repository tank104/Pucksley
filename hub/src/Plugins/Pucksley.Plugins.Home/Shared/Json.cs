using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pucksley.Plugins.Shared;

/// <summary>Forgiving accessors for device JSON (numbers that arrive as strings, 0/1 booleans, missing keys).</summary>
internal static class Json
{
    public static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The node at a dotted path ("result.device_on", "meters.0.power"), or null.</summary>
    public static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out int i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string? Str(JsonNode? node, string? path = null)
    {
        var n = path is null ? node : At(node, path);
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => null,
        };
    }

    public static double? Num(JsonNode? node, string? path = null)
    {
        var n = path is null ? node : At(node, path);
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            // Read from the JSON text: GetValue<double>() throws for values built from an int or long
            JsonValueKind.Number => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            JsonValueKind.String when double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static bool? Bool(JsonNode? node, string? path = null)
    {
        var n = path is null ? node : At(node, path);
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Num(v) != 0,
            JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() switch
            {
                "true" or "on" or "1" or "yes" => true,
                "false" or "off" or "0" or "no" => false,
                _ => null,
            },
            _ => null,
        };
    }
}
