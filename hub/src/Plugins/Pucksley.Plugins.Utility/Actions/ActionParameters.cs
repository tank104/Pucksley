using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Actions;

internal enum ParameterType { String, Number, Integer, Boolean }

/// <summary>One parameter of a custom action, from a <c>name:type:description</c> line.</summary>
internal sealed record ActionParameter(string Name, ParameterType Type, string? Description, bool Optional)
{
    public string JsonType => Type switch
    {
        ParameterType.Number => "number",
        ParameterType.Integer => "integer",
        ParameterType.Boolean => "boolean",
        _ => "string",
    };

    /// <summary>Scripts also get each value as an environment variable: <c>PUCK_LEVEL</c>.</summary>
    public string EnvironmentName => "PUCK_" + Name.ToUpperInvariant();
}

/// <summary>Parses parameter lines, builds the JSON schema, and reads Claude's arguments back as text.</summary>
internal static partial class ActionParameters
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    /// <summary>
    /// One parameter per line: <c>name:type:description</c>, type string|number|integer|boolean (string
    /// when left out), optional when the name ends in <c>?</c> (<c>level?:integer:0-100</c>). Blank lines
    /// and <c>#</c> comments are skipped. Throws <see cref="FormatException"/> naming the bad line.
    /// </summary>
    public static IReadOnlyList<ActionParameter> Parse(string? text)
    {
        var result = new List<ActionParameter>();
        var lines = (text ?? "").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(':', 3);
            var name = parts[0].Trim();
            bool optional = name.EndsWith('?');
            if (optional) name = name[..^1].TrimEnd();
            if (!NameRegex().IsMatch(name))
                throw new FormatException($"parameter line {n + 1}: '{parts[0].Trim()}' isn't a valid name (letters, digits and _)");

            var type = ParameterType.String;
            string? description = null;
            if (parts.Length > 1)
            {
                var second = parts[1].Trim();
                if (TryParseType(second, out var t))
                {
                    type = t;
                    description = parts.Length > 2 ? parts[2] : null;
                }
                else if (second.Length == 0) description = parts.Length > 2 ? parts[2] : null; // "name::description"
                else description = string.Join(':', parts[1..]);                               // "name:description"
            }
            if (result.Exists(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"parameter line {n + 1}: '{name}' is listed twice");
            result.Add(new ActionParameter(name, type, string.IsNullOrWhiteSpace(description) ? null : description.Trim(), optional));
        }
        return result;
    }

    static bool TryParseType(string text, out ParameterType type)
    {
        switch (text.ToLowerInvariant())
        {
            case "string" or "str" or "text": type = ParameterType.String; return true;
            case "number" or "float" or "double" or "decimal": type = ParameterType.Number; return true;
            case "integer" or "int" or "long": type = ParameterType.Integer; return true;
            case "boolean" or "bool": type = ParameterType.Boolean; return true;
            default: type = default; return false;
        }
    }

    /// <summary><c>{"type":"object","properties":{…},"required":[…]}</c>.</summary>
    public static JsonObject Schema(IReadOnlyList<ActionParameter> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            var property = new JsonObject { ["type"] = p.JsonType };
            if (p.Description is not null) property["description"] = p.Description;
            properties[p.Name] = property;
            if (!p.Optional) required.Add(p.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>
    /// Claude's arguments as text, one per parameter (a missing optional one is ""). Numbers are
    /// formatted invariantly, integers rounded, booleans "true"/"false". Throws <see cref="PluginException"/>
    /// for a missing or mistyped value.
    /// </summary>
    public static Dictionary<string, string> ReadArguments(IReadOnlyList<ActionParameter> parameters, JsonObject arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            var node = Find(arguments, p.Name);
            if (node is null)
            {
                if (!p.Optional) throw new PluginException($"Missing '{p.Name}'.");
                values[p.Name] = "";
            }
            else values[p.Name] = Format(p, node);
        }
        return values;
    }

    static JsonNode? Find(JsonObject arguments, string name)
    {
        if (arguments.TryGetPropertyValue(name, out var node)) return node;
        foreach (var (key, value) in arguments)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    static string Format(ActionParameter p, JsonNode node)
    {
        var value = node as JsonValue;
        string? text = value is not null && value.TryGetValue<string>(out var s) ? s : null;
        switch (p.Type)
        {
            case ParameterType.String:
                return text ?? node.ToJsonString();
            case ParameterType.Number when TryNumber(value, text, out var number):
                return number.ToString("R", CultureInfo.InvariantCulture);
            case ParameterType.Integer when TryNumber(value, text, out var number):
                return Math.Round(number, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            case ParameterType.Boolean:
                if (value is not null && value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                switch (text?.Trim().ToLowerInvariant())
                {
                    case "true" or "yes" or "on" or "1": return "true";
                    case "false" or "no" or "off" or "0": return "false";
                }
                break;
        }
        var expected = p.Type switch
        {
            ParameterType.Integer => "a whole number",
            ParameterType.Number => "a number",
            _ => "true or false",
        };
        throw new PluginException($"'{p.Name}' should be {expected}.");
    }

    static bool TryNumber(JsonValue? value, string? text, out double number)
    {
        number = 0;
        if (value is null) return false;
        if (value.TryGetValue(out number)) return double.IsFinite(number);
        return text is not null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
}
