using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pucksley.Sdk;

/// <summary>Turns <see cref="ToolAttribute"/> methods into <see cref="ToolDefinition"/>s.</summary>
public static class ToolBuilder
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: true) },
    };

    static readonly JsonSerializerOptions ResultJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Every <see cref="ToolAttribute"/> method (public or not) on <paramref name="target"/>.</summary>
    public static IReadOnlyList<ToolDefinition> FromMethods(object target, string? defaultIcon = null)
    {
        var list = new List<ToolDefinition>();
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in target.GetType().GetMethods(flags).OrderBy(m => m.MetadataToken))
        {
            var attr = method.GetCustomAttribute<ToolAttribute>();
            if (attr is null) continue;
            list.Add(FromMethod(target, method, attr, defaultIcon));
        }
        return list;
    }

    /// <summary>A tool from one method.</summary>
    public static ToolDefinition FromMethod(object? target, MethodInfo method, ToolAttribute attr, string? defaultIcon = null)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var parameters = method.GetParameters();
        var nullability = new NullabilityInfoContext();
        foreach (var p in parameters)
        {
            if (IsInjected(p.ParameterType)) continue;
            var schema = SchemaFor(p.ParameterType, out bool nullableValue);
            var pa = p.GetCustomAttribute<ParamAttribute>();
            if (pa is not null)
            {
                schema["description"] = pa.Description;
                if (pa.Values is { Length: > 0 }) schema["enum"] = new JsonArray(pa.Values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                if (!double.IsNaN(pa.Min)) schema["minimum"] = pa.Min;
                if (!double.IsNaN(pa.Max)) schema["maximum"] = pa.Max;
            }
            var name = SnakeCase(p.Name!);
            properties[name] = schema;
            bool nullableRef = !p.ParameterType.IsValueType && nullability.Create(p).WriteState == NullabilityState.Nullable;
            if (!(nullableValue || nullableRef || p.HasDefaultValue)) required.Add(name);
        }
        var input = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) input["required"] = required;

        return new ToolDefinition
        {
            Name = attr.Name,
            Description = attr.Description,
            InputSchema = input,
            Confirm = attr.Confirm,
            Icon = attr.Icon ?? defaultIcon,
            Handler = invocation => InvokeAsync(target, method, parameters, invocation),
        };
    }

    static bool IsInjected(Type t) => t == typeof(CancellationToken) || t == typeof(ToolCaller) || t == typeof(ToolInvocation);

    static JsonObject SchemaFor(Type type, out bool nullable)
    {
        nullable = false;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            nullable = true;
            type = underlying;
        }
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return new JsonObject { ["type"] = "number" };
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type).Select(n => (JsonNode)JsonValue.Create(SnakeCase(n))!).ToArray();
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(names) };
        }
        if (type == typeof(JsonObject) || type == typeof(JsonNode) || type == typeof(JsonElement))
            return new JsonObject { ["type"] = "object" };
        var element = type.IsArray ? type.GetElementType() :
            type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[0] : null;
        if (element is not null)
            return new JsonObject { ["type"] = "array", ["items"] = SchemaFor(element, out _) };
        throw new NotSupportedException($"Tool parameter type {type.Name} isn't supported.");
    }

    static async Task<ToolResult> InvokeAsync(object? target, MethodInfo method, ParameterInfo[] parameters, ToolInvocation inv)
    {
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken)) { args[i] = inv.Cancellation; continue; }
            if (p.ParameterType == typeof(ToolCaller)) { args[i] = inv.Caller; continue; }
            if (p.ParameterType == typeof(ToolInvocation)) { args[i] = inv; continue; }
            var name = SnakeCase(p.Name!);
            JsonNode? node = null;
            if (!inv.Arguments.TryGetPropertyValue(name, out node) && !inv.Arguments.TryGetPropertyValue(p.Name!, out node))
                node = null;
            if (node is null)
            {
                if (p.HasDefaultValue) args[i] = p.DefaultValue;
                else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null) args[i] = null;
                else return ToolResult.Error($"Missing required argument '{name}'.");
                continue;
            }
            try
            {
                args[i] = Convert(node, p.ParameterType);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException)
            {
                return ToolResult.Error($"Argument '{name}' isn't valid: {e.Message}");
            }
        }

        try
        {
            object? result = method.Invoke(target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var t = task.GetType();
                result = t.IsGenericType ? t.GetProperty("Result")!.GetValue(task) : null;
                // Task (non-generic) at runtime can still be Task<VoidTaskResult>: treat as void
                if (result is not null && result.GetType().Name == "VoidTaskResult") result = null;
            }
            return ToResult(result);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            return FromException(e.InnerException);
        }
        catch (Exception e)
        {
            return FromException(e);
        }
    }

    static ToolResult FromException(Exception e) => e switch
    {
        PluginException pe => ToolResult.Error(pe.Message),
        OperationCanceledException => ToolResult.Error("That didn't finish in time (or was cancelled)."),
        HttpRequestException he => ToolResult.Error($"Couldn't reach the device or service ({he.Message})."),
        _ => ToolResult.Error($"{e.GetType().Name}: {e.Message}"),
    };

    /// <summary>Wraps whatever a tool method returned.</summary>
    public static ToolResult ToResult(object? value) => value switch
    {
        null => ToolResult.Ok("Done."),
        ToolResult r => r,
        string s => ToolResult.Ok(s.Length == 0 ? "Done." : s),
        JsonNode n => ToolResult.Ok(n.ToJsonString()),
        _ => ToolResult.Ok(JsonSerializer.Serialize(value, value.GetType(), ResultJson)),
    };

    static object? Convert(JsonNode node, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(JsonObject)) return node as JsonObject ?? throw new FormatException("expected an object");
        if (underlying == typeof(JsonNode)) return node;
        if (underlying == typeof(JsonElement)) return JsonSerializer.SerializeToElement(node);
        if (underlying == typeof(string))
            return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        if (underlying == typeof(bool) && node is JsonValue bv && bv.TryGetValue<string>(out var bs))
            return bs.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1";
        // A single value where a list is expected: wrap it
        if ((underlying.IsArray || (underlying.IsGenericType && typeof(IEnumerable).IsAssignableFrom(underlying))) && node is not JsonArray)
            node = new JsonArray(node.DeepClone());
        return node.Deserialize(underlying, Json);
    }

    /// <summary><c>roomName</c> → <c>room_name</c>.</summary>
    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
