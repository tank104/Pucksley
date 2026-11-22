namespace Pucksley.Linux;

/// <summary>
/// One hub per person: a second launch finds the first one's lock and only opens its page. The
/// lock is a file held open in the session's runtime folder, so it vanishes with the process
/// (and with the session) and can never go stale.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    readonly FileStream _lock;

    SingleInstance(FileStream held) => _lock = held;

    static string Folder
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
        }
    }

    static string LockFile => Path.Combine(Folder, "pucksley.lock");
    static string UrlFile => Path.Combine(Folder, "pucksley.url");

    /// <summary>The lock, or null when another copy of the hub holds it.</summary>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            return new SingleInstance(new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null; // held by the running hub
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Leaves the hub's address for a second launch to open.</summary>
    public void Publish(string url)
    {
        try { File.WriteAllText(UrlFile, url); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the default address will do */ }
    }

    /// <summary>The running hub's address, if it left one.</summary>
    public static string? RunningUrl()
    {
        try
        {
            return File.Exists(UrlFile) && File.ReadAllText(UrlFile).Trim() is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { File.Delete(UrlFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing to remove */ }
        _lock.Dispose();
    }
}
