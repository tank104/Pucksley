using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Memory;

/// <summary>
/// Remembers facts the household tells it (they go into Claude's instructions on every turn) and keeps
/// simple named lists. Everything lives in memory and in <c>memory.json</c>.
/// </summary>
public sealed partial class MemoryPlugin : PuckPlugin<NoSettings>
{
    const int InstructionFacts = 60;
    const int InstructionFactLength = 200;
    const int MaxFacts = 1000;
    const int MaxFactLength = 500;
    const int MaxItemLength = 200;

    readonly object _gate = new(); // guards _state and _loaded
    MemoryState _state = new();
    bool _loaded;
    volatile string? _instructions; // rebuilt on every change: read on every turn, so it must be cheap

    public override string Id => "memory";
    public override string Name => "Memory & lists";
    public override string Description => "Remembers things the household asks it to (they're in Claude's instructions on every turn), and keeps simple lists: shopping, to do, packing…";
    public override string Icon => "note";
    public override bool EnabledByDefault => true;

    /// <summary>"Things the household asked you to remember:\n- …" (the latest 60), or null when there are none.</summary>
    public override string? Instructions
    {
        get
        {
            if (!_loaded && Context is not null)
                lock (_gate) EnsureLoaded();
            return _instructions;
        }
    }

    string StatePath => Path.Combine(Context.DataDirectory, "memory.json");

    public override Task StartAsync(CancellationToken cancel)
    {
        lock (_gate) EnsureLoaded();
        UpdateStatus();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ facts

    [Tool("memory_remember", "Remember a fact or preference for later; remembered things are in your instructions on every turn. E.g. \"Sam is allergic to peanuts\", \"the spare key is under the blue pot\".")]
    public ToolResult Remember([Param("The fact, as a short sentence")] string fact)
    {
        var text = Clean(fact, MaxFactLength) ?? throw new PluginException("What should I remember?");
        lock (_gate)
        {
            EnsureLoaded();
            var key = NameMatcher.Normalize(text);
            if (_state.Facts.Find(f => NameMatcher.Normalize(f.Text) == key) is { } known)
                return ToolResult.Ok($"I already remember that: {TextUtil.Sentence(known.Text)}", "Remembered");
            _state.Facts.Add(new MemoryFact { Text = text, Added = Context.Now });
            if (_state.Facts.Count > MaxFacts) _state.Facts.RemoveRange(0, _state.Facts.Count - MaxFacts);
            Changed();
        }
        UpdateStatus();
        return ToolResult.Ok($"Remembered: {TextUtil.Sentence(text)}", "Remembered");
    }

    [Tool("memory_forget", "Forget a remembered fact, found by a few words about it (\"wifi password\"). \"everything\" forgets them all (asks first).")]
    public async Task<ToolResult> Forget(
        [Param("A few words about what to forget")] string about,
        ToolCaller caller,
        CancellationToken cancel)
    {
        if (NameMatcher.Normalize(about) is "everything" or "all" or "all of it" or "all facts" or "everything you remember")
        {
            int count;
            lock (_gate)
            {
                EnsureLoaded();
                count = _state.Facts.Count;
            }
            if (count == 0) return "There's nothing to forget.";
            if (!await Context.ConfirmAsync($"Forget all {TextUtil.Plural(count, "thing")} I remember?", caller, cancel: cancel))
                return "OK, I haven't forgotten anything.";
            lock (_gate)
            {
                _state.Facts.Clear();
                Changed();
            }
            UpdateStatus();
            return ToolResult.Ok($"Forgot all {TextUtil.Plural(count, "thing")} I remembered.", "Forgot everything");
        }

        MemoryFact gone;
        lock (_gate)
        {
            EnsureLoaded();
            if (_state.Facts.Count == 0) throw new PluginException("I haven't been asked to remember anything.");
            gone = TextMatch.Best(about, _state.Facts, f => f.Text)
                   ?? throw new PluginException($"I don't remember anything about '{about.Trim()}'. I remember: {Summary(_state.Facts)}");
            _state.Facts.Remove(gone);
            Changed();
        }
        UpdateStatus();
        return ToolResult.Ok($"Forgot: {TextUtil.Sentence(gone.Text)}", "Forgot 1 thing");
    }

    [Tool("memory_list", "Everything you've been asked to remember.")]
    public string ListFacts()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var facts = _state.Facts;
            if (facts.Count == 0) return "I haven't been asked to remember anything yet.";
            var shown = facts.TakeLast(100).ToList();
            var head = facts.Count > shown.Count
                ? $"I remember {facts.Count} things; the latest {shown.Count}:"
                : $"I remember {TextUtil.Plural(facts.Count, "thing")}:";
            return head + "\n" + string.Join("\n", shown.Select((f, i) => $"{i + 1}. {f.Text}"));
        }
    }

    // ------------------------------------------------------------------ lists

    [Tool("list_add", "Add items to a list (shopping, to do, packing…). The list is created if it doesn't exist yet.")]
    public ToolResult ListAdd(
        [Param("List name, e.g. \"shopping\"")] string list,
        [Param("Items to add, e.g. [\"milk\", \"eggs\"]")] string[] items)
    {
        var wanted = SplitItems(items);
        if (wanted.Count == 0) throw new PluginException("What should I add?");
        var added = new List<string>();
        var already = new List<string>();
        NamedList target;
        int count;
        lock (_gate)
        {
            EnsureLoaded();
            target = FindList(list, create: true)!;
            foreach (var item in wanted)
            {
                var key = NameMatcher.Normalize(item);
                if (target.Items.Exists(i => NameMatcher.Normalize(i) == key)) already.Add(item);
                else
                {
                    target.Items.Add(item);
                    added.Add(item);
                }
            }
            target.Updated = Context.Now;
            count = target.Items.Count;
            Changed();
        }
        UpdateStatus();
        if (added.Count == 0)
            return ToolResult.Ok($"{TextUtil.Capitalize(TextUtil.JoinAnd(already))} {(already.Count == 1 ? "is" : "are")} already on the {target.Name} list.", TextUtil.Truncate($"{TextUtil.Capitalize(target.Name)}: no change", 24));
        var text = $"Added {TextUtil.JoinAnd(added)} to the {target.Name} list ({TextUtil.Plural(count, "item")}).";
        if (already.Count > 0) text += $" Already on it: {TextUtil.JoinAnd(already)}.";
        return ToolResult.Ok(text, TextUtil.Truncate($"Added {added.Count} to {target.Name}", 24));
    }

    [Tool("list_remove", "Remove items from a list (bought, packed or done).")]
    public ToolResult ListRemove(
        [Param("List name, e.g. \"shopping\"")] string list,
        [Param("Items to remove")] string[] items)
    {
        var wanted = SplitItems(items);
        if (wanted.Count == 0) throw new PluginException("What should I remove?");
        var removed = new List<string>();
        var missing = new List<string>();
        NamedList target;
        int left;
        lock (_gate)
        {
            EnsureLoaded();
            target = FindList(list, create: false) ?? throw NoSuchList(list);
            foreach (var item in wanted)
            {
                var key = NameMatcher.Normalize(item);
                var match = target.Items.Find(i => NameMatcher.Normalize(i) == key) ?? TextMatch.Best(item, target.Items, i => i, 0.6);
                if (match is null) missing.Add(item);
                else
                {
                    target.Items.Remove(match);
                    removed.Add(match);
                }
            }
            left = target.Items.Count;
            if (removed.Count > 0)
            {
                target.Updated = Context.Now;
                Changed();
            }
        }
        if (removed.Count == 0)
            throw new PluginException($"{TextUtil.Capitalize(TextUtil.JoinAnd(missing))} {(missing.Count == 1 ? "isn't" : "aren't")} on the {target.Name} list. " +
                                      (left == 0 ? "It's empty." : $"It has: {string.Join(", ", target.Items)}."));
        UpdateStatus();
        var text = $"Removed {TextUtil.JoinAnd(removed)} from the {target.Name} list ({left} left).";
        if (missing.Count > 0) text += $" Not on it: {TextUtil.JoinAnd(missing)}.";
        return ToolResult.Ok(text, TextUtil.Truncate($"Removed {removed.Count} from {target.Name}", 24));
    }

    [Tool("list_show", "Read out what's on a list.")]
    public ToolResult ListShow([Param("List name, e.g. \"shopping\"")] string list)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var target = FindList(list, create: false);
            if (target is null)
            {
                if (_state.Lists.Count == 0) return "There are no lists yet.";
                throw NoSuchList(list);
            }
            var activity = TextUtil.Truncate($"{TextUtil.Capitalize(target.Name)}: {TextUtil.Plural(target.Items.Count, "item")}", 24);
            return target.Items.Count == 0
                ? ToolResult.Ok($"The {target.Name} list is empty.", activity)
                : ToolResult.Ok($"{TextUtil.Capitalize(target.Name)} list ({target.Items.Count}): {string.Join(", ", target.Items)}.", activity);
        }
    }

    [Tool("list_clear", "Empty a list and delete it.", Confirm = true)]
    public ToolResult ListClear([Param("List name, e.g. \"shopping\"")] string list)
    {
        NamedList target;
        lock (_gate)
        {
            EnsureLoaded();
            target = FindList(list, create: false) ?? throw NoSuchList(list);
            _state.Lists.Remove(target);
            Changed();
        }
        UpdateStatus();
        return ToolResult.Ok($"Cleared the {target.Name} list ({TextUtil.Plural(target.Items.Count, "item")}).", TextUtil.Truncate($"Cleared {target.Name}", 24));
    }

    [Tool("lists", "The lists there are, with how many items each has.")]
    public string Lists()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _state.Lists.Count == 0
                ? "There are no lists yet."
                : "Lists: " + string.Join(", ", _state.Lists.Select(l => $"{l.Name} ({l.Items.Count})")) + ".";
        }
    }

    // ------------------------------------------------------------------ helpers (call under the lock)

    /// <summary>A list by loose name ("shopping list" = "shopping"); creates it when asked to.</summary>
    NamedList? FindList(string spoken, bool create)
    {
        var name = ListName(spoken);
        if (name.Length == 0) throw new PluginException("Which list?");
        // Creating needs a closer match, so "packing" doesn't land on an existing "parking" list.
        var found = _state.Lists.Find(l => l.Name == name) ?? NameMatcher.Best(name, _state.Lists, l => l.Name, create ? 0.8 : 0.6);
        if (found is not null || !create) return found;
        var list = new NamedList { Name = name, Updated = Context.Now };
        _state.Lists.Add(list);
        return list;
    }

    PluginException NoSuchList(string spoken) => new(_state.Lists.Count == 0
        ? "There are no lists yet."
        : $"I can't find a '{spoken.Trim()}' list. Lists: {string.Join(", ", _state.Lists.Select(l => l.Name))}.");

    /// <summary>"the Shopping List" → "shopping", "To-Do list" → "to do", "groceries" → "shopping".</summary>
    internal static string ListName(string spoken)
    {
        var words = NameMatcher.Normalize(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[0] is "the" or "my" or "our" or "a") words.RemoveAt(0);
        while (words.Count > 1 && words[^1] is "list" or "lists") words.RemoveAt(words.Count - 1);
        var name = string.Join(' ', words);
        return name switch
        {
            "todo" or "todos" or "to dos" or "things to do" => "to do",
            "grocery" or "groceries" or "grocery shopping" => "shopping",
            _ => name,
        };
    }

    /// <summary>Items as Claude sent them, also split on commas ("milk, eggs" as one string), cleaned and de-duplicated.</summary>
    static List<string> SplitItems(IEnumerable<string>? items)
    {
        var result = new List<string>();
        foreach (var raw in items ?? [])
            foreach (var part in (raw ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                if (Clean(part, MaxItemLength)?.TrimEnd('.') is { Length: > 0 } item &&
                    !result.Exists(r => NameMatcher.Normalize(r) == NameMatcher.Normalize(item)))
                    result.Add(item);
        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One line, single spaces, capped; null when empty.</summary>
    static string? Clean(string? text, int max)
    {
        var s = Whitespace().Replace(text ?? "", " ").Trim();
        return s.Length == 0 ? null : TextUtil.Truncate(s, max);
    }

    static string Summary(List<MemoryFact> facts) =>
        string.Join("; ", facts.TakeLast(10).Select(f => TextUtil.Truncate(f.Text, 60))) + (facts.Count > 10 ? "; …" : "") + ".";

    void Changed()
    {
        RebuildInstructions();
        try
        {
            JsonFile.Save(StatePath, _state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(e, "Couldn't save {Path}.", StatePath);
        }
    }

    void RebuildInstructions() =>
        _instructions = _state.Facts.Count == 0
            ? null
            : "Things the household asked you to remember:\n" +
              string.Join("\n", _state.Facts.TakeLast(InstructionFacts).Select(f => "- " + TextUtil.Truncate(f.Text, InstructionFactLength)));

    void EnsureLoaded()
    {
        if (_loaded) return;
        var state = JsonFile.Load<MemoryState>(StatePath, Log) ?? new MemoryState();
        state.Facts = (state.Facts ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        state.Lists = (state.Lists ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();
        foreach (var l in state.Lists) l.Items = (l.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        _state = state;
        RebuildInstructions();
        _loaded = true;
    }

    void UpdateStatus()
    {
        int facts, lists;
        lock (_gate)
        {
            facts = _state.Facts.Count;
            lists = _state.Lists.Count;
        }
        SetStatus(StatusLevel.Ok, facts == 0 && lists == 0
            ? "Nothing remembered yet"
            : $"{TextUtil.Plural(facts, "fact")}, {TextUtil.Plural(lists, "list")}");
    }
}

/// <summary>The whole <c>memory.json</c>.</summary>
internal sealed class MemoryState
{
    public List<MemoryFact> Facts { get; set; } = [];
    public List<NamedList> Lists { get; set; } = [];
}

internal sealed class MemoryFact
{
    public string Text { get; set; } = "";
    public DateTimeOffset Added { get; set; }
}

internal sealed class NamedList
{
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = [];
    public DateTimeOffset Updated { get; set; }
}
