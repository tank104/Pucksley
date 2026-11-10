using System.Globalization;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;

namespace Pucksley.Plugins.Heating;

internal sealed record FloorState(string Name, double? ReadingC, double? SetpointC, string? Call, string? Mode, bool InSafeState, IReadOnlyList<string> Faults)
{
    public bool CallingForHeat => string.Equals(Call, "heat", StringComparison.OrdinalIgnoreCase);
}

internal sealed record GroupOverride(string Kind, DateTimeOffset? ExpiresAt);

internal sealed record GroupState(string Name, string? State, double OffsetC, GroupOverride? Override);

internal sealed record HeatPumpState(
    string? LinkHealth, string? CurrentMode, string? OperatingMode, double? FlowTempC, double? Hc1SetpointC, long FaultBitmap, bool DewpointAlarm, bool SummerMode);

internal sealed record HistoryRow(DateTimeOffset At, double? ReadingC, double? SetpointC, string? Call);

/// <summary>What <c>GET /state</c> returns, read forgivingly (missing fields are null/empty, never an error).</summary>
internal sealed record HeatingState(
    string? DeviceId,
    DateTimeOffset? LastReportedAt,
    string? SystemMode,
    double? SetpointC,
    bool IsStaleConfig,
    IReadOnlyList<FloorState> Floors,
    IReadOnlyList<GroupState> Groups,
    HeatPumpState? HeatPump,
    IReadOnlyList<string> Faults)
{
    static readonly string[] FloorOrder = ["ground", "middle", "top"];
    static readonly string[] GroupOrder = ["ground", "middle_sun", "middle_shade", "top_sun", "top_shade"];

    public static HeatingState Parse(string json)
    {
        var root = Json.Parse(json) as JsonObject ?? throw new FormatException("The heating API didn't send a JSON object.");
        var floors = (root["floors"] as JsonObject ?? [])
            .Select(kv => new FloorState(
                kv.Key,
                Json.Num(kv.Value, "reading_c"),
                Json.Num(kv.Value, "setpoint_effective_c"),
                Json.Str(kv.Value, "call"),
                Json.Str(kv.Value, "mode"),
                Json.Bool(kv.Value, "in_safe_state") == true,
                ReadFaults(kv.Value?["active_faults"])))
            .OrderBy(f => Rank(FloorOrder, f.Name)).ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var groups = (root["groups"] as JsonObject ?? [])
            .Select(kv => new GroupState(
                kv.Key,
                Json.Str(kv.Value, "state"),
                Json.Num(kv.Value, "offset_c") ?? 0,
                kv.Value?["active_override"] is JsonObject o && Json.Str(o, "kind") is { Length: > 0 } kind
                    ? new GroupOverride(kind, Time(Json.Str(o, "expires_at")))
                    : null))
            .OrderBy(g => Rank(GroupOrder, g.Name)).ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
        var hp = root["heat_pump"] is JsonObject h
            ? new HeatPumpState(
                Json.Str(h, "link_health"), Json.Str(h, "current_mode"), Json.Str(h, "operating_mode"),
                Json.Num(h, "current_flow_temp_c"), Json.Num(h, "hc1_setpoint_c"),
                (long)(Json.Num(h, "fault_bitmap") ?? 0), Json.Bool(h, "dewpoint_alarm") == true, Json.Bool(h, "summer_mode") == true)
            : null;
        return new HeatingState(
            Json.Str(root, "deviceId"),
            Time(Json.Str(root, "lastReportedAt")),
            Json.Str(root, "systemMode"),
            Json.Num(root, "setpoint_c"),
            Json.Bool(root, "isStaleConfig") == true,
            floors, groups, hp,
            ReadFaults(root["active_faults"]));
    }

    /// <summary>Faults may be strings or objects; either way, a short label each.</summary>
    static IReadOnlyList<string> ReadFaults(JsonNode? node) => node is JsonArray list
        ? list.Select(f => f switch
            {
                JsonValue v => Json.Str(v),
                JsonObject o => Json.Str(o, "message") ?? Json.Str(o, "code") ?? Json.Str(o, "kind") ?? Json.Str(o, "name") ?? o.ToJsonString(),
                _ => null,
            })
            .OfType<string>().Where(s => s.Length > 0).ToList()
        : [];

    static int Rank(string[] order, string name) => Array.IndexOf(order, name) is var i and >= 0 ? i : order.Length;

    static DateTimeOffset? Time(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static IReadOnlyList<HistoryRow> ParseHistory(string json) =>
        (Json.At(Json.Parse(json), "rows") as JsonArray ?? [])
            .Select(r => Time(Json.Str(r, "at")) is DateTimeOffset at
                ? new HistoryRow(at, Json.Num(r, "readingC"), Json.Num(r, "setpointEffectiveC"), Json.Str(r, "call"))
                : null)
            .OfType<HistoryRow>()
            .OrderBy(r => r.At)
            .ToList();
}
