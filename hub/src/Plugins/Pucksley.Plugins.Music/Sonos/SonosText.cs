namespace Pucksley.Plugins.Sonos;

/// <summary>Sentences and summaries for Claude (short: they cost tokens and are often spoken).</summary>
public static class SonosText
{
    public const int MaxContext = 400;

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)].TrimEnd() + "…";

    /// <summary>For the puck's activity line (≤ 24 characters).</summary>
    public static string Activity(string s) => Clip(s, 24);

    /// <summary>The average volume of a group's members, if all are known.</summary>
    public static int? GroupVolume(IEnumerable<SonosPlayer> members, IReadOnlyDictionary<string, SonosVolume> volumes)
    {
        var known = new List<int>();
        foreach (var m in members)
        {
            if (!volumes.TryGetValue(m.Uuid, out var v)) return null;
            known.Add(v.Volume);
        }
        return known.Count == 0 ? null : (int)Math.Round(known.Average());
    }

    /// <summary>
    /// For the system prompt: <c>Sonos rooms: Kitchen (playing "Teardrop" – Massive Attack, vol 25);
    /// Lounge + Dining (paused); Office (idle).</c> At most <see cref="MaxContext"/> characters.
    /// </summary>
    public static string Context(SonosTopology topology, IReadOnlyDictionary<string, SonosGroupState> states,
        IReadOnlyDictionary<string, SonosVolume> volumes)
    {
        foreach (var maxTitle in new[] { 40, 24, 12 })
        {
            var text = "Sonos rooms: " + string.Join("; ", topology.Groups.Select(g => ContextPart(g, maxTitle))) + ".";
            if (text.Length <= MaxContext) return text;
        }
        var last = "Sonos rooms: " + string.Join("; ", topology.Groups.Select(g => ContextPart(g, 12))) + ".";
        return Clip(last, MaxContext);

        string ContextPart(SonosGroup g, int maxTitle)
        {
            var state = states.GetValueOrDefault(g.Coordinator.Uuid);
            if (state is null) return $"{g.Name} (unknown)";
            if (!state.IsPlaying) return $"{g.Name} ({state.StateWord})";
            var what = state.WhatsOn(maxTitle);
            var vol = GroupVolume(g.Members, volumes);
            return $"{g.Name} (playing{(what is null ? "" : " " + what)}{(vol is null ? "" : $", vol {vol}")})";
        }
    }

    /// <summary>One line per group for <c>sonos_rooms</c>.</summary>
    public static string Rooms(SonosTopology topology, IReadOnlyDictionary<string, SonosGroupState> states,
        IReadOnlyDictionary<string, SonosVolume> volumes)
    {
        var lines = topology.Groups.Select(g =>
        {
            var state = states.GetValueOrDefault(g.Coordinator.Uuid);
            var what = state?.WhatsOn();
            var status = state is null ? "state unknown" : what is null ? state.StateWord : $"{state.StateWord} {what}";
            string vol;
            if (g.Members.Count == 1)
                vol = volumes.TryGetValue(g.Coordinator.Uuid, out var v) ? $", vol {v.Volume}{(v.Muted ? " (muted)" : "")}" : "";
            else
                vol = "; vol " + string.Join(", ", g.Members.Select(m =>
                    volumes.TryGetValue(m.Uuid, out var mv) ? $"{m.RoomName} {mv.Volume}{(mv.Muted ? " (muted)" : "")}" : $"{m.RoomName} ?"));
            return $"{g.Name}{(g.Members.Count > 1 ? " (group)" : "")}: {status}{vol}";
        });
        return string.Join("\n", lines);
    }

    /// <summary>For <c>sonos_now_playing</c>.</summary>
    public static string NowPlaying(SonosGroup group, SonosGroupState state, IReadOnlyDictionary<string, SonosVolume> volumes)
    {
        var t = state.Track;
        string body;
        if (state.Source == SonosSource.Tv) body = $"{state.StateWord} the TV";
        else if (state.Source == SonosSource.LineIn) body = $"{state.StateWord} line-in";
        else if (t.Title is null)
            body = state.Source == SonosSource.None ? "nothing playing" : $"{state.StateWord}{(state.SourceLabel is { } src ? $" ({src})" : "")}";
        else
        {
            body = $"{state.StateWord} \"{t.Title}\"";
            if (t.Artist is not null) body += $" by {t.Artist}";
            if (t.Album is not null) body += $", from {t.Album}";
            if (state.Position is { } pos && state.Duration is { } dur && dur > TimeSpan.Zero)
                body += $" ({SonosTime.Short(pos)} of {SonosTime.Short(dur)})";
            if (state.SourceLabel is { } src) body += state.Source == SonosSource.Radio ? $", on {src}" : $", via {src}";
        }
        var vol = GroupVolume(group.Members, volumes);
        var muted = group.Members.All(m => volumes.TryGetValue(m.Uuid, out var v) && v.Muted);
        return $"{group.Name}: {body}.{(vol is null ? "" : $" Volume {vol}{(muted ? " (muted)" : "")}.")}";
    }
}
