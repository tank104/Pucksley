using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Common;

/// <summary>Writes the arguments of a D-Bus call.</summary>
internal delegate void BodyWriter(ref MessageWriter writer);

/// <summary>
/// A D-Bus connection that is opened when first needed and reopened if it drops: the session bus
/// (media players, the desktop portal, notifications) or the system bus (logind). Calls take a
/// timeout, so a service that never answers becomes an error rather than a stuck tool.
/// </summary>
internal sealed class Bus
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The desktop session's bus. Absent when the hub runs outside a login session.</summary>
    public static Bus Session { get; } = new(system: false);

    /// <summary>The system bus (logind, UPower).</summary>
    public static Bus SystemWide { get; } = new(system: true);

    readonly bool _system;
    readonly SemaphoreSlim _connecting = new(1, 1);
    DBusConnection? _connection;

    Bus(bool system) => _system = system;

    string Name => _system ? "The system bus" : "The desktop session's message bus";

    /// <summary>Whether there's a bus to try at all.</summary>
    public bool Exists => !string.IsNullOrEmpty(_system ? DBusAddress.System : DBusAddress.Session);

    /// <summary>The open connection (signals are watched on it directly).</summary>
    public async Task<DBusConnection> ConnectAsync(CancellationToken cancel)
    {
        if (Volatile.Read(ref _connection) is { } open) return open;
        await _connecting.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_connection is { } raced) return raced;
            var address = _system ? DBusAddress.System : DBusAddress.Session;
            if (string.IsNullOrEmpty(address)) throw new PluginException($"{Name} isn't available (the hub isn't running in a desktop session).");
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(DefaultTimeout, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DBusExceptionBase or TimeoutException or IOException or global::System.Net.Sockets.SocketException)
            {
                connection.Dispose();
                throw new PluginException($"{Name} isn't answering ({e.Message}).", e);
            }
            _connection = connection;
            // Dropped (the bus restarted, the session ended): the next call opens a new one
            _ = connection.DisconnectedAsync().ContinueWith(_ =>
            {
                Interlocked.CompareExchange(ref _connection, null, connection);
                connection.Dispose();
            }, TaskScheduler.Default);
            return connection;
        }
        finally
        {
            _connecting.Release();
        }
    }

    /// <summary>Calls a method and reads its reply.</summary>
    public async Task<T> CallAsync<T>(string destination, string path, string @interface, string member, MessageValueReader<T> reader,
        string? signature = null, BodyWriter? body = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var connection = await ConnectAsync(cancel).ConfigureAwait(false);
        var message = Compose(connection, destination, path, @interface, member, signature, body);
        return await Guard(connection.CallMethodAsync(message, reader), $"{@interface}.{member}", timeout, cancel).ConfigureAwait(false);
    }

    /// <summary>Calls a method whose reply carries nothing of interest.</summary>
    public async Task CallAsync(string destination, string path, string @interface, string member,
        string? signature = null, BodyWriter? body = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var connection = await ConnectAsync(cancel).ConfigureAwait(false);
        var message = Compose(connection, destination, path, @interface, member, signature, body);
        await Guard(Completed(connection.CallMethodAsync(message)), $"{@interface}.{member}", timeout, cancel).ConfigureAwait(false);
    }

    /// <summary>Reads one property (org.freedesktop.DBus.Properties.Get).</summary>
    public Task<VariantValue> GetAsync(string destination, string path, string @interface, string property, TimeSpan? timeout = null, CancellationToken cancel = default) =>
        CallAsync(destination, path, "org.freedesktop.DBus.Properties", "Get", static (m, _) => m.GetBodyReader().ReadVariantValue(), "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(@interface);
                w.WriteString(property);
            }, timeout, cancel);

    /// <summary>Reads every property of an interface (org.freedesktop.DBus.Properties.GetAll).</summary>
    public Task<Dictionary<string, VariantValue>> GetAllAsync(string destination, string path, string @interface, TimeSpan? timeout = null, CancellationToken cancel = default) =>
        CallAsync(destination, path, "org.freedesktop.DBus.Properties", "GetAll", static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(), "s",
            (ref MessageWriter w) => w.WriteString(@interface), timeout, cancel);

    /// <summary>The names on the bus right now (org.mpris.MediaPlayer2.spotify, …).</summary>
    public async Task<string[]> ListNamesAsync(CancellationToken cancel = default)
    {
        var connection = await ConnectAsync(cancel).ConfigureAwait(false);
        return await Guard(connection.ListServicesAsync(), "ListNames", null, cancel).ConfigureAwait(false);
    }

    static MessageBuffer Compose(DBusConnection connection, string destination, string path, string @interface, string member, string? signature, BodyWriter? body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    static async Task<bool> Completed(Task task)
    {
        await task.ConfigureAwait(false);
        return true;
    }

    static async Task<T> Guard<T>(Task<T> call, string what, TimeSpan? timeout, CancellationToken cancel)
    {
        try
        {
            return await call.WaitAsync(timeout ?? DefaultTimeout, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new PluginException($"{what} didn't answer in time.");
        }
    }

    /// <summary>True for the errors a service gives when it, or the thing asked for, isn't there.</summary>
    public static bool IsMissing(Exception e) => e is DBusErrorReplyException
    {
        ErrorName: "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" or "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownObject" or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.UnknownProperty"
            or "org.freedesktop.DBus.Error.InvalidArgs" or "org.freedesktop.DBus.Error.NotSupported",
    };
}
