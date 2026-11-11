using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Core.Settings;

/// <summary>
/// Describes a settings class for the web page (fields from <see cref="SettingAttribute"/>), and
/// moves values to and from the page with secrets masked.
/// </summary>
public static class SettingsSchema
{
    /// <summary>What the page shows (and sends back) for a secret that has a value.</summary>
    public const string SecretMask = "••••••••";

    public static JsonObject Describe(Type type)
    {
        var fields = new JsonArray();
        foreach (var (prop, attr) in Properties(type))
        {
            var key = Key(prop);
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var f = new JsonObject
            {
                ["key"] = key,
                ["label"] = attr.Label,
                ["help"] = attr.Help,
                ["placeholder"] = attr.Placeholder,
                ["secret"] = attr.Secret,
                ["advanced"] = attr.Advanced,
            };
            var item = SecretWalker.ListItemType(prop.PropertyType);
            if (item is not null)
            {
                f["type"] = "list";
                f["item"] = Describe(item);
            }
            else if (t == typeof(List<string>) || t == typeof(string[]))
            {
                f["type"] = "lines";
            }
            else if (t == typeof(bool)) f["type"] = "bool";
            else if (t == typeof(int) || t == typeof(long)) f["type"] = "int";
            else if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) f["type"] = "number";
            else if (t.IsEnum)
            {
                f["type"] = "select";
                f["options"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)n).ToArray());
            }
            else if (attr.Options is { Length: > 0 })
            {
                f["type"] = attr.Pictures ? "pictures" : "select";
                f["options"] = new JsonArray(attr.Options.Select(o => (JsonNode)o).ToArray());
            }
            else f["type"] = attr.Multiline ? "text" : "string";
            fields.Add(f);
        }
        return new JsonObject { ["fields"] = fields };
    }

    /// <summary>
    /// Values for the page: only the <see cref="SettingAttribute"/> properties (in list rows too), and
    /// secrets that have a value come out as <see cref="SecretMask"/>. Everything else (a puck's token,
    /// a plugin's own <c>[Secret]</c> refresh token, its bookkeeping) never leaves the hub.
    /// </summary>
    public static JsonObject ToUi(object settings)
    {
        var type = settings.GetType();
        var json = JsonSerializer.SerializeToNode(settings, type, SettingsStore.Json)!.AsObject();
        var shown = OnlyShown(json, type);
        SecretWalker.Transform(shown, type, s => string.IsNullOrEmpty(s) ? "" : SecretMask);
        return shown;
    }

    static JsonObject OnlyShown(JsonObject json, Type type)
    {
        var result = new JsonObject();
        foreach (var (prop, _) in Properties(type))
        {
            var key = Key(prop);
            if (!TryGet(json, key, out var value)) continue;
            var item = SecretWalker.ListItemType(prop.PropertyType);
            if (item is not null && value is JsonArray rows)
                result[key] = new JsonArray(rows.Select(r => r is JsonObject o ? OnlyShown(o, item) : r?.DeepClone()).ToArray());
            else if (SecretWalker.IsSettingsClass(prop.PropertyType) && value is JsonObject nested)
                result[key] = OnlyShown(nested, prop.PropertyType);
            else
                result[key] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// The page's values as a settings object: its <see cref="SettingAttribute"/> properties applied
    /// onto <paramref name="current"/>. Properties the page doesn't show keep their current values
    /// (it can't change them), secrets still showing the mask keep theirs, and each list row lands on
    /// the current row at the same place, so a row's hidden values and masked secrets survive too
    /// (the page replaces lists whole, and knows rows are matched by position).
    /// </summary>
    public static object FromUi(JsonObject ui, Type type, object current)
    {
        var currentJson = JsonSerializer.SerializeToNode(current, type, SettingsStore.Json)!.AsObject();
        Merge(currentJson, ui, type);
        return currentJson.Deserialize(type, SettingsStore.Json) ?? Activator.CreateInstance(type)!;
    }

    static void Merge(JsonObject target, JsonObject incoming, Type type)
    {
        foreach (var (prop, attr) in Properties(type))
        {
            var key = Key(prop);
            if (!TryGet(incoming, key, out var value)) continue;
            var existing = target.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Key ?? key;
            if (attr.Secret && value is JsonValue v && v.TryGetValue<string>(out var s) && s == SecretMask) continue;
            var item = SecretWalker.ListItemType(prop.PropertyType);
            if (item is not null && value is JsonArray rows)
            {
                var currentRows = target[existing] as JsonArray;
                var merged = new JsonArray();
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is not JsonObject row)
                    {
                        merged.Add(rows[i]?.DeepClone());
                        continue;
                    }
                    var baseRow = currentRows is not null && i < currentRows.Count && currentRows[i] is JsonObject cur ? (JsonObject)cur.DeepClone() : new JsonObject();
                    Merge(baseRow, row, item);
                    merged.Add(baseRow);
                }
                target[existing] = merged;
            }
            else if (SecretWalker.IsSettingsClass(prop.PropertyType) && value is JsonObject nested)
            {
                var baseObj = target[existing] is JsonObject cur ? (JsonObject)cur.DeepClone() : new JsonObject();
                Merge(baseObj, nested, prop.PropertyType);
                target[existing] = baseObj;
            }
            else
            {
                target[existing] = value?.DeepClone();
            }
        }
    }

    static bool TryGet(JsonObject o, string key, out JsonNode? value)
    {
        if (o.TryGetPropertyValue(key, out value)) return true;
        foreach (var kv in o)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        value = null;
        return false;
    }

    static IEnumerable<(PropertyInfo Prop, SettingAttribute Attr)> Properties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => (Prop: p, Attr: p.GetCustomAttribute<SettingAttribute>()!))
            .Where(x => x.Attr is not null && x.Prop.CanWrite)
            .OrderBy(x => x.Attr.Order)
            .ThenBy(x => x.Prop.MetadataToken);

    static string Key(PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);
}
