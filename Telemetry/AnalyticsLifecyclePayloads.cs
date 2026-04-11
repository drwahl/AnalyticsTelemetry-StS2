using System.Text.Json.Serialization;

namespace AnalyticsTelemetry.Telemetry;

public sealed record RunContextSnapshotPayload(
    [property: JsonPropertyName("accountKey")] string AccountKey,
    [property: JsonPropertyName("profileFolder")] string ProfileFolder,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("actIndex")] int ActIndex,
    [property: JsonPropertyName("actId")] string ActId,
    [property: JsonPropertyName("mapDepth")] int MapDepth,
    [property: JsonPropertyName("ascension")] int Ascension,
    [property: JsonPropertyName("partySize")] int PartySize,
    [property: JsonPropertyName("partyPlayerKeys")] IReadOnlyList<string> PartyPlayerKeys,
    /// <summary>Counts of visited map points by <c>map_point_type</c> / <c>room_type</c> from <c>map_point_history</c>.</summary>
    [property: JsonPropertyName("roomVisitsByType")] IReadOnlyDictionary<string, int>? RoomVisitsByType,
    /// <summary>Parsed from run save when present (same probe as <c>run_gold</c>).</summary>
    [property: JsonPropertyName("gold")] int? Gold);

public sealed record RunGoldPayload(
    [property: JsonPropertyName("accountKey")] string AccountKey,
    [property: JsonPropertyName("profileFolder")] string ProfileFolder,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("gold")] int Gold,
    [property: JsonPropertyName("previousGold")] int? PreviousGold);

public sealed record CombatStartedPayload(
    [property: JsonPropertyName("combatOrdinal")] int CombatOrdinal,
    [property: JsonPropertyName("actIndex")] int ActIndex,
    [property: JsonPropertyName("actId")] string ActId,
    [property: JsonPropertyName("mapDepth")] int MapDepth,
    [property: JsonPropertyName("runMode")] string RunMode,
    [property: JsonPropertyName("partyPlayerKeys")] IReadOnlyList<string> PartyPlayerKeys);

public sealed record CombatEndedPayload(
    [property: JsonPropertyName("combatOrdinal")] int CombatOrdinal,
    [property: JsonPropertyName("durationSeconds")] double? DurationSeconds);
