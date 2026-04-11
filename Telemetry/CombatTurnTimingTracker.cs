using System.Text.Json.Serialization;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Estimates wall-clock duration per combat <b>side</b> segment (Player vs Enemy) from consecutive
/// history entries that expose <c>CurrentSide</c> / <c>RoundNumber</c>. A <b>Player</b> segment is a
/// practical stand-in for “time on that turn with a hand” (decision time until side flips).
/// </summary>
internal static class CombatTurnTimingTracker
{
    private static bool _haveSegment;
    private static string? _activeSide;
    private static string? _roundWhenSegmentStarted;
    private static DateTime _segmentStartUtc;

    /// <summary>Same side/round inference as segment timing (monster move ⇒ Enemy).</summary>
    internal static bool TryResolveSideRound(
        string sourceEntryTypeName,
        Dictionary<string, string?> fields,
        out string side,
        out string? round)
    {
        round = fields.TryGetValue("RoundNumber", out var r) ? r : null;
        if (fields.TryGetValue("CurrentSide", out var s) && !string.IsNullOrWhiteSpace(s))
        {
            side = s.Trim();
            return true;
        }

        if (sourceEntryTypeName == "MonsterPerformedMoveEntry")
        {
            side = "Enemy";
            return true;
        }

        side = "";
        return false;
    }

    /// <summary>Call after the triggering history line is written so NDJSON stays chronological.</summary>
    internal static void OnHistoryEntryAfterLog(DateTime occurredUtc, string sourceEntryTypeName, Dictionary<string, string?> fields)
    {
        if (!TryResolveSideRound(sourceEntryTypeName, fields, out var side, out var round))
            return;

        if (!_haveSegment)
        {
            StartSegment(occurredUtc, side, round);
            if (string.Equals(side, "Player", StringComparison.Ordinal))
                CombatEnergyFlowTracker.OnPlayerSegmentStarted(round);
            return;
        }

        var sideChanged = !string.Equals(side, _activeSide, StringComparison.Ordinal);
        var roundChanged = RoundChanged(_roundWhenSegmentStarted, round);

        if (!sideChanged && !roundChanged)
            return;

        var closedSide = _activeSide!;
        var closedRoundStart = _roundWhenSegmentStarted;
        var duration = (occurredUtc - _segmentStartUtc).TotalSeconds;
        var endReason = sideChanged ? "side_changed" : "round_advanced";

        TelemetryEventLog.WriteRaw(
            "combat_turn_segment",
            new CombatTurnSegmentPayload(
                Side: closedSide,
                RoundWhenStarted: closedRoundStart,
                RoundWhenEnded: round,
                DurationSeconds: duration,
                EndReason: endReason,
                SegmentStartUtc: _segmentStartUtc,
                SegmentEndUtc: occurredUtc,
                ClosedByHistoryEntry: sourceEntryTypeName),
            occurredUtc);

        if (string.Equals(closedSide, "Player", StringComparison.Ordinal))
        {
            CombatEnergyFlowTracker.FlushPlayerTurn(
                closedRoundStart,
                round,
                endReason,
                occurredUtc,
                sourceEntryTypeName);
        }

        StartSegment(occurredUtc, side, round);
        if (string.Equals(side, "Player", StringComparison.Ordinal))
            CombatEnergyFlowTracker.OnPlayerSegmentStarted(round);
    }

    private static void StartSegment(DateTime occurredUtc, string side, string? round)
    {
        _haveSegment = true;
        _activeSide = side;
        _roundWhenSegmentStarted = round;
        _segmentStartUtc = occurredUtc;
    }

    private static bool RoundChanged(string? previous, string? current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;
        return !string.Equals(previous.Trim(), current.Trim(), StringComparison.Ordinal);
    }
}

/// <summary>Wall-clock span for one continuous <c>CurrentSide</c> stretch in combat history.</summary>
public sealed record CombatTurnSegmentPayload(
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("roundWhenStarted")] string? RoundWhenStarted,
    [property: JsonPropertyName("roundWhenEnded")] string? RoundWhenEnded,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("endReason")] string EndReason,
    [property: JsonPropertyName("segmentStartUtc")] DateTime SegmentStartUtc,
    [property: JsonPropertyName("segmentEndUtc")] DateTime SegmentEndUtc,
    [property: JsonPropertyName("closedByHistoryEntry")] string? ClosedByHistoryEntry);
