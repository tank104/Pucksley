using System.Text;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Pucksley.Linux.Tray;

/// <summary>
/// An icon in the desktop's system tray, spoken as the StatusNotifierItem protocol on the session
/// bus (what KDE, Hyprland bars such as Omarchy's and Waybar, Sway bars, Xfce, Cinnamon, and GNOME
/// with the AppIndicator extension show), with its menu as com.canonical.dbusmenu.
/// </summary>
/// <remarks>
/// The tray belongs to another program (the panel), which can restart at any time, and may not be
/// there at all. So this keeps trying: it registers whenever a tray appears, and reconnects if the
/// session bus drops. With no tray the hub simply runs without an icon.
/// </remarks>
internal sealed class TrayIcon
{
    const string ItemPath = "/StatusNotifierItem";
    const string MenuPath = "/MenuBar";
    const string ItemInterface = "org.kde.StatusNotifierItem";
    const string MenuInterface = "com.canonical.dbusmenu";
    const string Watcher = "org.kde.StatusNotifierWatcher";
    const string Properties = "org.freedesktop.DBus.Properties";
    static readonly int[] IconSizes = [16, 22, 24, 32, 48, 64];

    readonly string _id;
    readonly ILogger _log;
    readonly object _gate = new();
    readonly List<(int Size, byte[] Argb)> _icons;
    DBusConnection? _connection;
    string _title;
    string _toolTip;
    uint _revision = 1;

    public TrayIcon(string id, string title, ILogger log, Func<int, byte[]> drawIcon)
    {
        _id = id;
        _title = title;
        _toolTip = title;
        _log = log;
        _icons = IconSizes.Select(s => (s, drawIcon(s))).ToList();
    }

    public TrayMenu Menu { get; } = new();

    /// <summary>A click on the icon itself.</summary>
    public Action? Activated { get; set; }

    /// <summary>True while a tray is showing the icon.</summary>
    public bool Shown { get; private set; }

    // ───────────────────────── Keeping it there

    /// <summary>Runs until <paramref name="stop"/>: connects, shows the icon, and puts it back whenever the tray or the bus comes and goes.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        if (string.IsNullOrEmpty(DBusAddress.Session))
        {
            _log.LogInformation("No desktop session bus, so no tray icon (the hub runs without one)");
            return;
        }
        bool told = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var connection = new DBusConnection(DBusAddress.Session!);
                await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                connection.AddMethodHandlers([new ItemHandler(this), new MenuHandler(this)]);
                string name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
                await connection.RequestNameAsync(name, RequestNameOptions.None).WaitAsync(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                lock (_gate) _connection = connection;

                // The tray appearing later (or again, after the panel restarts) is when to register
                using var watching = await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = "org.freedesktop.DBus",
                        Path = "/org/freedesktop/DBus",
                        Interface = "org.freedesktop.DBus",
                        Member = "NameOwnerChanged",
                        Arg0 = Watcher,
                    },
                    static (Message m, object? _) =>
                    {
                        var reader = m.GetBodyReader();
                        reader.ReadString();
                        reader.ReadString();
                        return reader.ReadString(); // the new owner: empty when the tray went away
                    },
                    (Notification<string> n) =>
                    {
                        if (!n.HasValue) return;
                        if (n.Value.Length == 0) Shown = false;
                        else _ = RegisterAsync(connection, name);
                    },
                    emitOnCapturedContext: false).ConfigureAwait(false);

                if (!await RegisterAsync(connection, name).ConfigureAwait(false) && !told)
                {
                    told = true;
                    _log.LogInformation("This desktop has no system tray yet; the icon will appear when one does. The hub's page is at its usual address meanwhile");
                }
                await connection.DisconnectedAsync().WaitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "The tray icon lost the session bus");
            }
            finally
            {
                lock (_gate) _connection = null;
                Shown = false;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task<bool> RegisterAsync(DBusConnection connection, string name)
    {
        try
        {
            MessageBuffer message;
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", "s");
                writer.WriteString(name);
                message = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
            await connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Shown = true;
            _log.LogInformation("The tray icon is showing");
            return true;
        }
        catch (Exception e) when (e is DBusExceptionBase or TimeoutException)
        {
            Shown = false;
            _log.LogDebug("No system tray to register with: {Message}", e.Message);
            return false;
        }
    }

    // ───────────────────────── Changes

    /// <summary>The text shown when the pointer rests on the icon.</summary>
    public void SetToolTip(string text)
    {
        lock (_gate)
        {
            if (_toolTip == text) return;
            _toolTip = text;
        }
        Emit(ItemPath, ItemInterface, "NewToolTip", null, null);
    }

    /// <summary>Tells the tray that a menu item's label, tick or greyness changed.</summary>
    public void MenuItemChanged(TrayMenuItem item)
    {
        uint revision;
        lock (_gate) revision = ++_revision;
        Emit(MenuPath, MenuInterface, "ItemsPropertiesUpdated", "a(ia{sv})a(ias)", (ref MessageWriter w) =>
        {
            var updated = w.WriteArrayStart(DBusType.Struct);
            w.WriteStructureStart();
            w.WriteInt32(item.Id);
            WriteProperties(ref w, TrayMenu.Properties(item));
            w.WriteArrayEnd(updated);
            var removed = w.WriteArrayStart(DBusType.Struct);
            w.WriteArrayEnd(removed);
        });
        // Hosts that don't follow property updates re-read the whole (small) menu
        Emit(MenuPath, MenuInterface, "LayoutUpdated", "ui", (ref MessageWriter w) =>
        {
            w.WriteUInt32(revision);
            w.WriteInt32(0);
        });
    }

    delegate void Body(ref MessageWriter writer);

    void Emit(string path, string @interface, string member, string? signature, Body? body)
    {
        DBusConnection? connection;
        lock (_gate) connection = _connection;
        if (connection is null) return;
        try
        {
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(null, path, @interface, member, signature);
                body?.Invoke(ref writer);
                connection.TrySendMessage(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }
        catch (Exception e) when (e is DBusExceptionBase or ObjectDisposedException or InvalidOperationException)
        {
            _log.LogDebug(e, "Couldn't tell the tray about a change");
        }
    }

    // ───────────────────────── Writing values

    static void WriteProperties(ref MessageWriter w, IEnumerable<(string Name, object Value)> properties)
    {
        var dictionary = w.WriteDictionaryStart();
        foreach (var (name, value) in properties)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(name);
            WriteVariant(ref w, value);
        }
        w.WriteDictionaryEnd(dictionary);
    }

    static void WriteVariant(ref MessageWriter w, object value)
    {
        switch (value)
        {
            case string s: w.WriteVariantString(s); break;
            case bool b: w.WriteVariantBool(b); break;
            case int i: w.WriteVariantInt32(i); break;
            case uint u: w.WriteVariantUInt32(u); break;
            default: throw new ArgumentException($"A tray value can't be a {value.GetType().Name}.");
        }
    }

    /// <summary>Pictures as a(iiay): width, height, and the pixels as alpha-red-green-blue bytes.</summary>
    static void WritePixmaps(ref MessageWriter w, IReadOnlyList<(int Size, byte[] Argb)> icons)
    {
        var array = w.WriteArrayStart(DBusType.Struct);
        foreach (var (size, argb) in icons)
        {
            w.WriteStructureStart();
            w.WriteInt32(size);
            w.WriteInt32(size);
            w.WriteArray(argb);
        }
        w.WriteArrayEnd(array);
    }

    /// <summary>One of the icon's properties as a variant; false when there's no such property.</summary>
    bool WriteItemProperty(ref MessageWriter w, string name)
    {
        string title, toolTip;
        lock (_gate) (title, toolTip) = (_title, _toolTip);
        switch (name)
        {
            case "Category": w.WriteVariantString("ApplicationStatus"); return true;
            case "Id": w.WriteVariantString(_id); return true;
            case "Title": w.WriteVariantString(title); return true;
            case "Status": w.WriteVariantString("Active"); return true;
            case "WindowId": w.WriteVariantInt32(0); return true;
            // No icon by name: the pixels below are used, so it looks the same under any icon theme
            case "IconName" or "OverlayIconName" or "AttentionIconName" or "AttentionMovieName" or "IconThemePath":
                w.WriteVariantString("");
                return true;
            case "IconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, _icons);
                return true;
            case "OverlayIconPixmap" or "AttentionIconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, []);
                return true;
            case "ToolTip":
                // (icon name, icon pixels, title, text)
                w.WriteSignature("(sa(iiay)ss)");
                w.WriteStructureStart();
                w.WriteString("");
                WritePixmaps(ref w, []);
                w.WriteString(title);
                w.WriteString(toolTip == title ? "" : toolTip);
                return true;
            case "ItemIsMenu": w.WriteVariantBool(false); return true;
            case "Menu": w.WriteVariantObjectPath(MenuPath); return true;
            default: return false;
        }
    }

    static readonly string[] ItemProperties =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
        "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu", "IconThemePath",
    ];

    /// <summary>A menu item as (ia{sv}av): its id, its properties, and its children (the root's are the items).</summary>
    void WriteMenuItem(ref MessageWriter w, int id, bool withChildren)
    {
        w.WriteStructureStart();
        w.WriteInt32(id);
        if (id == 0) WriteProperties(ref w, [("children-display", "submenu")]);
        else WriteProperties(ref w, Menu.Find(id) is { } item ? TrayMenu.Properties(item) : []);
        var children = w.WriteArrayStart(DBusType.Variant);
        if (id == 0 && withChildren)
        {
            foreach (var child in Menu.Items)
            {
                w.WriteSignature("(ia{sv}av)");
                WriteMenuItem(ref w, child.Id, false);
            }
        }
        w.WriteArrayEnd(children);
    }

    static void RunLater(Action? action, ILogger log)
    {
        if (action is null) return;
        // Off the bus's own thread: a click may open a browser or stop the hub
        Task.Run(() =>
        {
            try { action(); }
            catch (Exception e) { log.LogWarning(e, "A tray action failed"); }
        });
    }

    // ───────────────────────── What the tray asks

    sealed class ItemHandler(TrayIcon owner) : IPathMethodHandler
    {
        static readonly ReadOnlyMemory<byte> Xml = Encoding.UTF8.GetBytes("""
            <interface name="org.kde.StatusNotifierItem">
              <property name="Category" type="s" access="read"/>
              <property name="Id" type="s" access="read"/>
              <property name="Title" type="s" access="read"/>
              <property name="Status" type="s" access="read"/>
              <property name="WindowId" type="i" access="read"/>
              <property name="IconThemePath" type="s" access="read"/>
              <property name="Menu" type="o" access="read"/>
              <property name="ItemIsMenu" type="b" access="read"/>
              <property name="IconName" type="s" access="read"/>
              <property name="IconPixmap" type="a(iiay)" access="read"/>
              <property name="OverlayIconName" type="s" access="read"/>
              <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
              <property name="AttentionIconName" type="s" access="read"/>
              <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
              <property name="AttentionMovieName" type="s" access="read"/>
              <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
              <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
              <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
              <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
              <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
              <signal name="NewTitle"/>
              <signal name="NewIcon"/>
              <signal name="NewAttentionIcon"/>
              <signal name="NewOverlayIcon"/>
              <signal name="NewToolTip"/>
              <signal name="NewStatus"><arg name="status" type="s"/></signal>
            </interface>
            """);

        public string Path => ItemPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([Xml]);
                return default;
            }
            string member = request.MemberAsString ?? "";
            if (context.IsPropertiesInterfaceRequest)
            {
                var reader = request.GetBodyReader();
                switch (member)
                {
                    case "Get":
                        reader.ReadString();
                        string name = reader.ReadString();
                        var one = context.CreateReplyWriter("v");
                        try
                        {
                            if (!owner.WriteItemProperty(ref one, name))
                            {
                                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name}");
                                return default;
                            }
                            context.Reply(one.CreateMessage());
                        }
                        finally
                        {
                            one.Dispose();
                        }
                        return default;
                    case "GetAll":
                        var all = context.CreateReplyWriter("a{sv}");
                        try
                        {
                            var dictionary = all.WriteDictionaryStart();
                            foreach (var property in ItemProperties)
                            {
                                all.WriteDictionaryEntryStart();
                                all.WriteString(property);
                                owner.WriteItemProperty(ref all, property);
                            }
                            all.WriteDictionaryEnd(dictionary);
                            context.Reply(all.CreateMessage());
                        }
                        finally
                        {
                            all.Dispose();
                        }
                        return default;
                }
                context.ReplyUnknownMethodError();
                return default;
            }
            if (request.InterfaceAsString == ItemInterface)
            {
                switch (member)
                {
                    case "Activate" or "SecondaryActivate":
                        RunLater(owner.Activated, owner._log);
                        Reply(context);
                        return default;
                    // The tray draws the menu itself, from the menu object
                    case "ContextMenu" or "Scroll":
                        Reply(context);
                        return default;
                }
            }
            context.ReplyUnknownMethodError();
            return default;
        }
    }

    static void Reply(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        var writer = context.CreateReplyWriter(null!);
        try { context.Reply(writer.CreateMessage()); }
        finally { writer.Dispose(); }
    }

    sealed class MenuHandler(TrayIcon owner) : IPathMethodHandler
    {
        static readonly ReadOnlyMemory<byte> Xml = Encoding.UTF8.GetBytes("""
            <interface name="com.canonical.dbusmenu">
              <property name="Version" type="u" access="read"/>
              <property name="TextDirection" type="s" access="read"/>
              <property name="Status" type="s" access="read"/>
              <property name="IconThemePath" type="as" access="read"/>
              <method name="GetLayout">
                <arg name="parentId" type="i" direction="in"/><arg name="recursionDepth" type="i" direction="in"/><arg name="propertyNames" type="as" direction="in"/>
                <arg name="revision" type="u" direction="out"/><arg name="layout" type="(ia{sv}av)" direction="out"/>
              </method>
              <method name="GetGroupProperties">
                <arg name="ids" type="ai" direction="in"/><arg name="propertyNames" type="as" direction="in"/><arg name="properties" type="a(ia{sv})" direction="out"/>
              </method>
              <method name="GetProperty"><arg name="id" type="i" direction="in"/><arg name="name" type="s" direction="in"/><arg name="value" type="v" direction="out"/></method>
              <method name="Event"><arg name="id" type="i" direction="in"/><arg name="eventId" type="s" direction="in"/><arg name="data" type="v" direction="in"/><arg name="timestamp" type="u" direction="in"/></method>
              <method name="EventGroup"><arg name="events" type="a(isvu)" direction="in"/><arg name="idErrors" type="ai" direction="out"/></method>
              <method name="AboutToShow"><arg name="id" type="i" direction="in"/><arg name="needUpdate" type="b" direction="out"/></method>
              <method name="AboutToShowGroup"><arg name="ids" type="ai" direction="in"/><arg name="updatesNeeded" type="ai" direction="out"/><arg name="idErrors" type="ai" direction="out"/></method>
              <signal name="ItemsPropertiesUpdated"><arg name="updatedProps" type="a(ia{sv})"/><arg name="removedProps" type="a(ias)"/></signal>
              <signal name="LayoutUpdated"><arg name="revision" type="u"/><arg name="parent" type="i"/></signal>
              <signal name="ItemActivationRequested"><arg name="id" type="i"/><arg name="timestamp" type="u"/></signal>
            </interface>
            """);

        public string Path => MenuPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([Xml]);
                return default;
            }
            string member = request.MemberAsString ?? "";
            var reader = request.GetBodyReader();
            if (context.IsPropertiesInterfaceRequest)
            {
                switch (member)
                {
                    case "Get":
                        reader.ReadString();
                        string name = reader.ReadString();
                        var one = context.CreateReplyWriter("v");
                        try
                        {
                            if (!WriteMenuProperty(ref one, name))
                            {
                                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name}");
                                return default;
                            }
                            context.Reply(one.CreateMessage());
                        }
                        finally
                        {
                            one.Dispose();
                        }
                        return default;
                    case "GetAll":
                        var all = context.CreateReplyWriter("a{sv}");
                        try
                        {
                            var dictionary = all.WriteDictionaryStart();
                            foreach (var property in new[] { "Version", "TextDirection", "Status", "IconThemePath" })
                            {
                                all.WriteDictionaryEntryStart();
                                all.WriteString(property);
                                WriteMenuProperty(ref all, property);
                            }
                            all.WriteDictionaryEnd(dictionary);
                            context.Reply(all.CreateMessage());
                        }
                        finally
                        {
                            all.Dispose();
                        }
                        return default;
                }
                context.ReplyUnknownMethodError();
                return default;
            }
            if (request.InterfaceAsString != MenuInterface)
            {
                context.ReplyUnknownMethodError();
                return default;
            }

            switch (member)
            {
                case "GetLayout":
                {
                    int parent = reader.ReadInt32();
                    uint revision;
                    lock (owner._gate) revision = owner._revision;
                    var w = context.CreateReplyWriter("u(ia{sv}av)");
                    try
                    {
                        w.WriteUInt32(revision);
                        owner.WriteMenuItem(ref w, parent, withChildren: true);
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
                case "GetGroupProperties":
                {
                    var ids = reader.ReadArrayOfInt32();
                    // No ids asked for means all of them
                    var items = ids.Length == 0 ? owner.Menu.Items : ids.Select(owner.Menu.Find).Where(i => i is not null).Select(i => i!).ToList();
                    var w = context.CreateReplyWriter("a(ia{sv})");
                    try
                    {
                        var array = w.WriteArrayStart(DBusType.Struct);
                        foreach (var item in items)
                        {
                            w.WriteStructureStart();
                            w.WriteInt32(item.Id);
                            WriteProperties(ref w, TrayMenu.Properties(item));
                        }
                        w.WriteArrayEnd(array);
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
                case "GetProperty":
                {
                    int id = reader.ReadInt32();
                    string name = reader.ReadString();
                    var value = owner.Menu.Find(id) is { } item ? TrayMenu.Properties(item).FirstOrDefault(p => p.Name == name).Value : null;
                    if (value is null)
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"Item {id} has no property {name}");
                        return default;
                    }
                    var w = context.CreateReplyWriter("v");
                    try
                    {
                        WriteVariant(ref w, value);
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
                case "Event":
                {
                    int id = reader.ReadInt32();
                    string what = reader.ReadString();
                    Clicked(id, what);
                    Reply(context);
                    return default;
                }
                case "EventGroup":
                {
                    var end = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(end))
                    {
                        reader.AlignStruct();
                        int id = reader.ReadInt32();
                        string what = reader.ReadString();
                        reader.ReadVariantValue();
                        reader.ReadUInt32();
                        Clicked(id, what);
                    }
                    var w = context.CreateReplyWriter("ai");
                    try
                    {
                        w.WriteArray(Array.Empty<int>());
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
                case "AboutToShow":
                {
                    var w = context.CreateReplyWriter("b");
                    try
                    {
                        w.WriteBool(false);
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
                case "AboutToShowGroup":
                {
                    var w = context.CreateReplyWriter("aiai");
                    try
                    {
                        w.WriteArray(Array.Empty<int>());
                        w.WriteArray(Array.Empty<int>());
                        context.Reply(w.CreateMessage());
                    }
                    finally
                    {
                        w.Dispose();
                    }
                    return default;
                }
            }
            context.ReplyUnknownMethodError();
            return default;
        }

        void Clicked(int id, string what)
        {
            if (what != "clicked") return;
            if (owner.Menu.Find(id) is { Enabled: true, Separator: false } item) RunLater(item.Clicked, owner._log);
        }

        static bool WriteMenuProperty(ref MessageWriter w, string name)
        {
            switch (name)
            {
                case "Version": w.WriteVariantUInt32(3); return true;
                case "TextDirection": w.WriteVariantString("ltr"); return true;
                case "Status": w.WriteVariantString("normal"); return true;
                case "IconThemePath":
                    w.WriteSignature("as");
                    w.WriteArray(Array.Empty<string>());
                    return true;
                default: return false;
            }
        }
    }
}
