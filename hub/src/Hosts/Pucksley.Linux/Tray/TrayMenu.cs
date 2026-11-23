namespace Pucksley.Linux.Tray;

/// <summary>One line of the tray icon's menu.</summary>
internal sealed class TrayMenuItem
{
    /// <summary>Set by <see cref="TrayMenu"/>: 1, 2, 3… (0 is the menu itself).</summary>
    public int Id { get; internal set; }

    public string Label { get; set; } = "";

    /// <summary>False greys it out (a line that only says something, like who's connected).</summary>
    public bool Enabled { get; set; } = true;

    public bool Separator { get; init; }

    /// <summary>Null for a plain item; true or false makes it a tick box.</summary>
    public bool? Checked { get; set; }

    public Action? Clicked { get; init; }
}

/// <summary>The tray icon's menu: a short, flat list.</summary>
internal sealed class TrayMenu
{
    readonly List<TrayMenuItem> _items = [];

    public IReadOnlyList<TrayMenuItem> Items => _items;

    public TrayMenuItem Add(TrayMenuItem item)
    {
        item.Id = _items.Count + 1;
        _items.Add(item);
        return item;
    }

    public TrayMenuItem AddSeparator() => Add(new TrayMenuItem { Separator = true });

    public TrayMenuItem? Find(int id) => id >= 1 && id <= _items.Count ? _items[id - 1] : null;

    /// <summary>
    /// The properties a menu host asks for, as (name, value): strings, booleans and whole numbers.
    /// An underscore in a label marks a keyboard shortcut there, so a real one is doubled.
    /// </summary>
    public static IEnumerable<(string Name, object Value)> Properties(TrayMenuItem item)
    {
        if (item.Separator)
        {
            yield return ("type", "separator");
            yield break;
        }
        yield return ("label", item.Label.Replace("_", "__"));
        yield return ("enabled", item.Enabled);
        yield return ("visible", true);
        if (item.Checked is bool ticked)
        {
            yield return ("toggle-type", "checkmark");
            yield return ("toggle-state", ticked ? 1 : 0);
        }
    }
}
