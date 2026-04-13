using System.Text.Json.Serialization;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Ordered list of <see cref="MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayStartedEntry"/> per
/// <c>handSequence</c> (same boundary as <c>combat_player_energy_turn</c>). Interleaved MP plays follow combat history order.
/// </summary>
internal static class HandCardPlayOrderTracker
{
    private static readonly object Gate = new();
    private static readonly List<HandCardPlayStepPayload> Buffer = [];

    internal static void ResetSession()
    {
        lock (Gate)
            Buffer.Clear();
    }

    internal static void OnCardPlayStarted(IReadOnlyDictionary<string, string?> fields, DateTime occurredUtc)
    {
        var card = CardDamageParsing.TryCardDisplayKey(fields) ?? "?";
        var playerKey = CardPlayOrderPlayerParsing.TryPlayerKey(fields);
        lock (Gate)
        {
            Buffer.Add(new HandCardPlayStepPayload(
                Order: Buffer.Count + 1,
                CardDisplay: card,
                PlayerKey: playerKey,
                OccurredUtc: occurredUtc));
        }
    }

    /// <summary>Call when a Player segment ends (before <c>combat_player_energy_turn</c>).</summary>
    internal static void FlushHand(int combatOrdinal, int handSequence, string? roundWhenEnded, DateTime segmentEndUtc)
    {
        List<HandCardPlayStepPayload> snapshot;
        lock (Gate)
        {
            if (Buffer.Count == 0)
                return;
            snapshot = [..Buffer];
            Buffer.Clear();
        }

        var scope = TelemetryScopeContext.Snapshot();
        TelemetryEventLog.WriteRaw(
            "hand_card_play_order",
            new HandCardPlayOrderPayload(
                CombatOrdinal: combatOrdinal,
                HandSequence: handSequence,
                RoundWhenEnded: roundWhenEnded,
                SegmentEndUtc: segmentEndUtc,
                PartyPlayerKeys: scope.PartyPlayerKeys,
                Plays: snapshot),
            segmentEndUtc);
    }

    /// <summary>Drops buffered plays without emit (new combat / clear metrics).</summary>
    internal static void ClearBuffer()
    {
        lock (Gate)
            Buffer.Clear();
    }

    /// <summary>If combat ends with unflushed plays, emit one line so the last hand is not lost.</summary>
    internal static void OnCombatEnded(int combatOrdinal)
    {
        List<HandCardPlayStepPayload> snapshot;
        lock (Gate)
        {
            if (Buffer.Count == 0)
                return;
            snapshot = [..Buffer];
            Buffer.Clear();
        }

        var scope = TelemetryScopeContext.Snapshot();
        var utc = DateTime.UtcNow;
        TelemetryEventLog.WriteRaw(
            "hand_card_play_order",
            new HandCardPlayOrderPayload(
                CombatOrdinal: combatOrdinal > 0 ? combatOrdinal : scope.CombatOrdinal,
                HandSequence: scope.HandSequence,
                RoundWhenEnded: null,
                SegmentEndUtc: utc,
                PartyPlayerKeys: scope.PartyPlayerKeys,
                Plays: snapshot),
            utc);
    }
}

public sealed record HandCardPlayOrderPayload(
    [property: JsonPropertyName("combatOrdinal")] int CombatOrdinal,
    [property: JsonPropertyName("handSequence")] int HandSequence,
    [property: JsonPropertyName("roundWhenEnded")] string? RoundWhenEnded,
    [property: JsonPropertyName("segmentEndUtc")] DateTime SegmentEndUtc,
    [property: JsonPropertyName("partyPlayerKeys")] IReadOnlyList<string> PartyPlayerKeys,
    [property: JsonPropertyName("plays")] IReadOnlyList<HandCardPlayStepPayload> Plays);

public sealed record HandCardPlayStepPayload(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("cardDisplay")] string CardDisplay,
    [property: JsonPropertyName("playerKey")] string? PlayerKey,
    [property: JsonPropertyName("occurredUtc")] DateTime OccurredUtc);
