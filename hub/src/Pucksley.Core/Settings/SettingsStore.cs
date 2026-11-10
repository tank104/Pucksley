using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pucksley.Sdk;

namespace Pucksley.Core.Settings;

/// <summary>Where the hub keeps its files.</summary>
public static class HubPaths
{
    /// <summary><c>PUCKSLEY_DATA</c>, else %AppData%\Pucksley (Windows) / ~/.config/pucksley, or /data in a container.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string PluginDataDir(string pluginId) => Path.Combine(DataDir, "plugin-data", pluginId);
    public static string PluginsDir => Path.Combine(DataDir, "plugins");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("PUCKSLEY_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pucksley");
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true") return "/data";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "pucksley");
    }
}

/// <summary>
/// Encrypts secrets at rest: DPAPI (this Windows user) on Windows; elsewhere AES-GCM with a key file
/// next to the settings (readable only by the hub's user).
/// </summary>
public sealed class SecretProtector
{
    const string Prefix = "enc:";
    readonly byte[]? _key;

    public SecretProtector(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(dataDir, "secret.key");
        try
        {
            if (File.Exists(path)) _key = File.ReadAllBytes(path);
            if (_key is not { Length: 32 })
            {
                _key = RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(dataDir);
                File.WriteAllBytes(path, _key);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (IOException) { _key = null; }
        catch (UnauthorizedAccessException) { _key = null; }
    }

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            return Prefix + "d:" + Convert.ToBase64String(ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        if (_key is null) return plain;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + "a:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            var body = value[Prefix.Length..];
            if (body.StartsWith("d:") && OperatingSystem.IsWindows())
                return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(body[2..]), null, DataProtectionScope.CurrentUser));
            if (body.StartsWith("a:") && _key is not null)
            {
                var all = Convert.FromBase64String(body[2..]);
                var plain = new byte[all.Length - 28];
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { }
        return "";
    }
}

/// <summary>Loads and saves <see cref="HubSettings"/>; encrypts every <c>[Setting(Secret = true)]</c> property. Thread-safe.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    readonly object _gate = new();
    readonly string _path;
    HubSettings _current;

    public SecretProtector Secrets { get; }

    public SettingsStore(string? path = null)
    {
        _path = path ?? HubPaths.SettingsFile;
        Secrets = new SecretProtector(Path.GetDirectoryName(_path)!);
        _current = Load();
        bool dirty = false;
        if (string.IsNullOrEmpty(_current.HubId)) { _current.HubId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(); dirty = true; }
        if (string.IsNullOrEmpty(_current.AdminToken)) { _current.AdminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(); dirty = true; }
        if (string.IsNullOrEmpty(_current.HubName)) { _current.HubName = Environment.MachineName; dirty = true; }
        if (dirty) Save();
    }

    /// <summary>The settings (decrypted). Treat as read-only; change them with <see cref="Update"/>.</summary>
    public HubSettings Current { get { lock (_gate) return _current; } }

    /// <summary>Raised after a change is saved.</summary>
    public event Action? Changed;

    public void Update(Action<HubSettings> change)
    {
        lock (_gate)
        {
            change(_current);
            Save();
        }
        Changed?.Invoke();
    }

    void Save()
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(_current, Json)!.AsObject();
            SecretWalker.Transform(node, typeof(HubSettings), Secrets.Protect);
            // Pucks' tokens aren't [Setting]s but are secrets too
            if (node["pucks"] is JsonArray pucks)
                foreach (var p in pucks.OfType<JsonObject>())
                    if (p["token"]?.GetValue<string>() is { Length: > 0 } t) p["token"] = Secrets.Protect(t);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, node.ToJsonString(Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    HubSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HubSettings();
            var node = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (node is null) return new HubSettings();
            SecretWalker.Transform(node, typeof(HubSettings), Secrets.Unprotect);
            if (node["pucks"] is JsonArray pucks)
                foreach (var p in pucks.OfType<JsonObject>())
                    if (p["token"]?.GetValue<string>() is { Length: > 0 } t) p["token"] = Secrets.Unprotect(t);
            var s = node.Deserialize<HubSettings>(Json) ?? new HubSettings();
            s.Plugins = new Dictionary<string, PluginState>(s.Plugins, StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch (IOException) { }
            return new HubSettings();
        }
    }

    /// <summary>A plugin's settings object from its stored JSON (secrets decrypted). Missing → defaults.</summary>
    public object PluginSettings(string pluginId, Type type)
    {
        JsonObject? json;
        lock (_gate) json = _current.Plugins.TryGetValue(pluginId, out var st) ? st.Settings?.DeepClone().AsObject() : null;
        if (json is null) return Activator.CreateInstance(type)!;
        SecretWalker.Transform(json, type, Secrets.Unprotect);
        try { return json.Deserialize(type, Json) ?? Activator.CreateInstance(type)!; }
        catch (JsonException) { return Activator.CreateInstance(type)!; }
    }

    /// <summary>Store a plugin's settings object (secrets encrypted).</summary>
    public void SavePluginSettings(string pluginId, object settings)
    {
        var json = JsonSerializer.SerializeToNode(settings, settings.GetType(), Json)!.AsObject();
        SecretWalker.Transform(json, settings.GetType(), Secrets.Protect);
        Update(s =>
        {
            if (!s.Plugins.TryGetValue(pluginId, out var st)) s.Plugins[pluginId] = st = new PluginState();
            st.Settings = json;
        });
    }
}

/// <summary>Finds <c>[Setting(Secret = true)]</c> string properties in a JSON tree by the CLR type that produced it.</summary>
public static class SecretWalker
{
    public static void Transform(JsonObject node, Type type, Func<string, string> transform)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var child = node.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (child.Value is null) continue;
            var attr = prop.GetCustomAttribute<SettingAttribute>();
            bool secret = attr?.Secret == true || prop.GetCustomAttribute<SecretAttribute>() is not null;
            if (secret && prop.PropertyType == typeof(string) && child.Value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                node[child.Key] = transform(s);
                continue;
            }
            var itemType = ListItemType(prop.PropertyType);
            if (itemType is not null && child.Value is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>()) Transform(item, itemType, transform);
            }
            else if (IsSettingsClass(prop.PropertyType) && child.Value is JsonObject obj)
            {
                Transform(obj, prop.PropertyType, transform);
            }
        }
    }

    public static Type? ListItemType(Type t)
    {
        if (t.IsArray) return IsSettingsClass(t.GetElementType()!) ? t.GetElementType() : null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var e = t.GetGenericArguments()[0];
            return IsSettingsClass(e) ? e : null;
        }
        return null;
    }

    public static bool IsSettingsClass(Type t) =>
        t.IsClass && t != typeof(string) && !typeof(JsonNode).IsAssignableFrom(t) && !t.IsGenericType && t.Namespace?.StartsWith("System") != true;
}
