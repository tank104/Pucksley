using System.Globalization;
using System.Text.Json;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc.Windows;

/// <summary>
/// Sway's windows through <c>swaymsg</c>, and i3's through <c>i3-msg</c>: the two speak the same
/// protocol and describe the desktop as the same tree of outputs, workspaces and containers.
/// </summary>
internal sealed class SwayWindows(string program, AppNamer namer) : IWindowManager
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public bool CanSeeWindows => true;

    Task<string> TreeAsync(CancellationToken cancel) => Programs.OutputAsync(program, ["-t", "get_tree"], Timeout, cancel);

    public async Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) =>
        ParseTree(await TreeAsync(cancel).ConfigureAwait(false), namer).Select(w => w.Window).Where(w => w.ProcessId != Environment.ProcessId).ToList();

    public async Task<AppWindow?> ForegroundAsync(CancellationToken cancel) =>
        ParseTree(await TreeAsync(cancel).ConfigureAwait(false), namer).FirstOrDefault(w => w.Focused)?.Window;

    internal sealed record TreeWindow(AppWindow Window, bool Focused, ScreenRect Rect, string Output);

    /// <summary>
    /// The windows in the tree, most recently used first: every container lists its children in the
    /// order they last had the focus, so following those lists walks from the front to the back.
    /// </summary>
    internal static List<TreeWindow> ParseTree(string json, AppNamer namer)
    {
        using var doc = JsonDocument.Parse(json);
        var windows = new List<TreeWindow>();
        Walk(doc.RootElement, "", windows, namer);
        return windows;
    }

    static void Walk(JsonElement node, string output, List<TreeWindow> windows, AppNamer namer)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        string type = String(node, "type");
        if (type == "output") output = String(node, "name");
        // The scratchpad's hidden output holds windows nobody can see
        if (output == "__i3") return;

        if (node.TryGetProperty("pid", out var pidValue) && pidValue.ValueKind == JsonValueKind.Number && type is "con" or "floating_con")
        {
            string windowClass = String(node, "app_id");
            if (windowClass.Length == 0 && node.TryGetProperty("window_properties", out var x11) && x11.ValueKind == JsonValueKind.Object) windowClass = String(x11, "class");
            int pid = pidValue.GetInt32();
            var (processName, appName) = namer(windowClass, pid);
            string title = String(node, "name");
            if (title.Length > 0 && node.TryGetProperty("id", out var id) && id.TryGetInt64(out long handle))
                windows.Add(new TreeWindow(new AppWindow((nint)handle, title, pid, processName, appName),
                    node.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True, Rect(node), output));
            return;
        }

        var children = new List<JsonElement>();
        foreach (var list in new[] { "nodes", "floating_nodes" })
        {
            if (node.TryGetProperty(list, out var nodes) && nodes.ValueKind == JsonValueKind.Array) children.AddRange(nodes.EnumerateArray());
        }
        var order = node.TryGetProperty("focus", out var focus) && focus.ValueKind == JsonValueKind.Array
            ? focus.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Number).Select(i => i.GetInt64()).ToList()
            : [];
        long Id(JsonElement child) => child.ValueKind == JsonValueKind.Object && child.TryGetProperty("id", out var id) && id.TryGetInt64(out long n) ? n : -1;
        foreach (var child in children.OrderBy(c => order.IndexOf(Id(c)) is var at && at >= 0 ? at : int.MaxValue))
            Walk(child, output, windows, namer);
    }

    internal static List<ScreenOutput> ParseOutputs(string json, string? focusedOutput)
    {
        using var doc = JsonDocument.Parse(json);
        var outputs = new List<ScreenOutput>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return outputs;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || String(node, "type") != "output") continue;
            string name = String(node, "name");
            var rect = Rect(node);
            if (name.Length == 0 || name == "__i3" || rect.IsEmpty) continue;
            outputs.Add(new ScreenOutput(name, rect, name == focusedOutput));
        }
        return outputs;
    }

    static ScreenRect Rect(JsonElement node)
    {
        if (!node.TryGetProperty("rect", out var r) || r.ValueKind != JsonValueKind.Object) return default;
        int Get(string name) => r.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
        return new ScreenRect(Get("x"), Get("y"), Get("width"), Get("height"));
    }

    static string String(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public async Task<bool> FocusAsync(AppWindow window, CancellationToken cancel)
    {
        string reply = await Programs.OutputAsync(program, [$"[con_id={((long)window.Handle).ToString(CultureInfo.InvariantCulture)}]", "focus"], Timeout, cancel).ConfigureAwait(false);
        return reply.Contains("\"success\": true", StringComparison.Ordinal) || reply.Contains("\"success\":true", StringComparison.Ordinal);
    }

    public Task ShowDesktopAsync(CancellationToken cancel) =>
        throw new PluginException($"{(program == "swaymsg" ? "Sway" : "i3")} has no \"show desktop\": its windows fill the screen by design. I can bring a particular app to the front instead.");

    public Task<bool?> IsLockedAsync(CancellationToken cancel) => Task.FromResult<bool?>(null);

    public async Task<ScreenLayout?> LayoutAsync(CancellationToken cancel)
    {
        string json = await TreeAsync(cancel).ConfigureAwait(false);
        var focused = ParseTree(json, namer).FirstOrDefault(w => w.Focused);
        return new ScreenLayout(ParseOutputs(json, focused?.Output), focused?.Rect, focused?.Window.Title);
    }
}
