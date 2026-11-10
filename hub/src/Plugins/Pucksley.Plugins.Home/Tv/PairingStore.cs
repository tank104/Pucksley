using System.Text.Json;

namespace Pucksley.Plugins.Tv;

/// <summary>
/// Pairing keys the TVs hand out (LG client-key, Samsung token), per type and host, kept in
/// <c>tv-pairing.json</c> in the plugin's data folder. Thread-safe.
/// </summary>
internal sealed class PairingStore(string directory)
{
    readonly string _path = Path.Combine(directory, "tv-pairing.json");
    readonly object _lock = new();
    Dictionary<string, string>? _keys;

    public string? Get(string type, string host)
    {
        lock (_lock) return Load().GetValueOrDefault(Key(type, host));
    }

    public void Set(string type, string host, string value)
    {
        lock (_lock)
        {
            var keys = Load();
            keys[Key(type, host)] = value;
            Save(keys);
        }
    }

    public void Remove(string type, string host)
    {
        lock (_lock)
        {
            var keys = Load();
            if (keys.Remove(Key(type, host))) Save(keys);
        }
    }

    static string Key(string type, string host) => $"{type}:{host}".Trim().ToLowerInvariant();

    Dictionary<string, string> Load()
    {
        if (_keys is not null) return _keys;
        try
        {
            _keys = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _keys = null;
        }
        return _keys ??= new Dictionary<string, string>();
    }

    void Save(Dictionary<string, string> keys)
    {
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
