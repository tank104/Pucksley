using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Pucksley.Plugins;

/// <summary>Small JSON state files in a plugin's data directory, written atomically.</summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Reads <paramref name="path"/>, or returns null when it doesn't exist. A file that isn't valid
    /// JSON is moved aside (so a fresh one can be written) rather than silently overwritten; I/O errors
    /// propagate, because starting empty would overwrite the person's data on the next save.
    /// </summary>
    public static T? Load<T>(string path, ILogger log) where T : class
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException e)
        {
            var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try { File.Move(path, aside, overwrite: true); } catch (IOException) { }
            log.LogWarning(e, "{Path} isn't valid JSON; starting fresh (the old file was kept as {Aside}).", path, aside);
            return null;
        }
    }

    public static void Save<T>(string path, T value) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}

/// <summary>Crash-safe writes: write a temp file beside the target, flush it to disk, then rename it over the target.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }
            // The rename can briefly fail on Windows while a virus scanner or reader holds the target open.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, full, overwrite: true);
                    return;
                }
                catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20 * attempt);
                }
            }
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
