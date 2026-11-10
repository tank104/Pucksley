using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;

namespace Pucksley.Plugins.HomeAssistant;

/// <summary>
/// One Home Assistant entity as last seen. Immutable: the plugin swaps whole snapshots, so readers
/// never see a half-updated entity. <see cref="Attributes"/> is fully materialized and never mutated.
/// </summary>
internal sealed record HaEntity
{
    public required string EntityId { get; init; }

    public required string State { get; init; }

    public JsonObject Attributes { get; init; } = new();

    /// <summary>The entity's area (or its device's area), from <c>area_name()</c>; null when none.</summary>
    public string? Area { get; init; }

    public string Domain => EntityId.IndexOf('.') is var i and > 0 ? EntityId[..i] : EntityId;

    public string ObjectId => EntityId[(EntityId.IndexOf('.') + 1)..];

    /// <summary>The friendly name, else the object id with spaces ("kitchen_ceiling" → "kitchen ceiling").</summary>
    public string Name => Json.Str(Attributes, "friendly_name") is { Length: > 0 } n ? n : ObjectId.Replace('_', ' ');

    public bool Available => State is not ("unavailable" or "unknown" or "");

    /// <summary>On in the broad sense: anything but off/closed/idle-ish states.</summary>
    public bool IsActive => Available && State is not ("off" or "closed" or "idle" or "standby" or "docked" or "paused" or "locked" or "not_home");

    public string? Attr(string name) => Json.Str(Attributes, name);

    public double? AttrNum(string name) => Json.Num(Attributes, name);

    /// <summary>Reads one entry of <c>/api/states</c> (or a service call's changed states).</summary>
    public static HaEntity? FromJson(JsonNode? node, string? area = null)
    {
        var id = Json.Str(node, "entity_id");
        if (string.IsNullOrEmpty(id) || !id.Contains('.')) return null;
        var attributes = node?["attributes"] is JsonObject a ? (JsonObject)Materialize(a.DeepClone()) : new JsonObject();
        return new HaEntity { EntityId = id, State = Json.Str(node, "state") ?? "unknown", Attributes = attributes, Area = area };
    }

    /// <summary>Walks the tree once so lazily-built nodes are built now (reads are then thread-safe).</summary>
    static JsonNode Materialize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) if (child is not null) Materialize(child);
                break;
            case JsonArray arr:
                foreach (var child in arr) if (child is not null) Materialize(child);
                break;
        }
        return node;
    }
}
