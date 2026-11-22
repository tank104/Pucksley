using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Common;

/// <summary>
/// Desktop notifications (the pop-ups in the corner), through the notification service every
/// desktop runs. Without one, nothing is shown and nothing fails.
/// </summary>
internal static class DesktopNotifications
{
    const string Service = "org.freedesktop.Notifications";
    const string Path = "/org/freedesktop/Notifications";

    /// <summary>
    /// Shows a notification, or replaces the one with the id <paramref name="replaces"/>. Returns
    /// its id (0 when there's nowhere to show it).
    /// </summary>
    /// <param name="critical">Stays until dismissed, and shows over full-screen apps.</param>
    /// <param name="timeoutMs">How long it stays: -1 for the desktop's usual time, 0 for until dismissed.</param>
    /// <param name="actions">Buttons, as (key, label); "default" is a click on the notification itself.</param>
    public static async Task<uint> ShowAsync(string summary, string body, uint replaces = 0, bool critical = false, int timeoutMs = -1,
        IReadOnlyList<(string Key, string Label)>? actions = null, CancellationToken cancel = default)
    {
        if (!Bus.Session.Exists) return 0;
        var hints = new Dictionary<string, VariantValue>
        {
            ["urgency"] = VariantValue.Byte(critical ? (byte)2 : (byte)1),
            ["desktop-entry"] = VariantValue.String("pucksley"),
        };
        var buttons = (actions ?? []).SelectMany(a => new[] { a.Key, a.Label }).ToArray();
        try
        {
            return await Bus.Session.CallAsync(Service, Path, Service, "Notify", static (m, _) => m.GetBodyReader().ReadUInt32(), "susssasa{sv}i",
                (ref MessageWriter w) =>
                {
                    w.WriteString("Pucksley");
                    w.WriteUInt32(replaces);
                    w.WriteString("pucksley");
                    w.WriteString(summary);
                    w.WriteString(body);
                    w.WriteArray(buttons);
                    w.WriteDictionary(hints);
                    w.WriteInt32(timeoutMs);
                }, cancel: cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or PluginException)
        {
            return 0; // no notification service on this desktop
        }
    }

    /// <summary>
    /// Calls <paramref name="onAction"/> with (notification id, action key) whenever a notification's
    /// button — or the notification itself, key "default" — is clicked. Dispose the result to stop.
    /// </summary>
    public static async Task<IDisposable?> WatchActionsAsync(Action<uint, string> onAction, CancellationToken cancel = default)
    {
        if (!Bus.Session.Exists) return null;
        try
        {
            var connection = await Bus.Session.ConnectAsync(cancel).ConfigureAwait(false);
            return await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Path = Path, Interface = Service, Member = "ActionInvoked" },
                static (Message m, object? _) =>
                {
                    var reader = m.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Key: reader.ReadString());
                },
                (Notification<(uint Id, string Key)> n) => { if (n.HasValue) onAction(n.Value.Id, n.Value.Key); },
                emitOnCapturedContext: false).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or PluginException)
        {
            return null;
        }
    }
}
