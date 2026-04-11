using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Tracks player energy mutations via <see cref="MegaCrit.Sts2.Core.Commands.PlayerCmd"/> and rolls them
/// up per <b>player</b> combat-history segment (same boundary as <c>combat_turn_segment</c> when side is Player).
/// Turn-start <c>SetEnergy</c> often runs before the next history line shows <c>CurrentSide=Player</c>; those
/// steps are held in a pre-player buffer and merged when the player segment starts.
/// </summary>
internal static class CombatEnergyFlowTracker
{
    private static readonly object Gate = new();
    private static string? _lastHistorySide;

    private static readonly List<CombatEnergyFlowStepPayload> Steps = [];
    private static readonly List<CombatEnergyFlowStepPayload> PrePlayerSteps = [];

    /// <summary>Clears staged energy steps (e.g. after <see cref="TelemetryMetricsStore.ClearAllHistoricMetrics"/>).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Steps.Clear();
            PrePlayerSteps.Clear();
            _lastHistorySide = null;
        }
    }

    /// <summary>Updated when we successfully resolve side from a combat history line.</summary>
    internal static void NotifyHistorySide(string side)
    {
        lock (Gate)
            _lastHistorySide = side;
    }

    internal static void OnPlayerCmdEnergy(string op, decimal amount, Player player)
    {
        var cs = player.PlayerCombatState;
        if (cs is null)
            return;

        var utc = DateTime.UtcNow;
        var pk = PlayerKeyUtil.FromNetId(player.NetId);
        TelemetryScopeContext.NoteEnergyPlayerKey(pk);
        var step = new CombatEnergyFlowStepPayload(
            Op: op,
            Amount: amount,
            EnergyAfter: cs.Energy,
            MaxEnergyAfter: cs.MaxEnergy,
            Utc: utc,
            PlayerKey: pk);

        lock (Gate)
        {
            if (string.Equals(_lastHistorySide, "Player", StringComparison.Ordinal))
                Steps.Add(step);
            else
                PrePlayerSteps.Add(step);
        }
    }

    internal static void OnPlayerSegmentStarted(string? _)
    {
        lock (Gate)
        {
            if (PrePlayerSteps.Count == 0)
                return;
            Steps.InsertRange(0, PrePlayerSteps);
            PrePlayerSteps.Clear();
        }
    }

    internal static void FlushPlayerTurn(
        string? roundWhenStarted,
        string? roundWhenEnded,
        string endReason,
        DateTime endUtc,
        string? closedByHistoryEntry)
    {
        List<CombatEnergyFlowStepPayload> snapshot;
        lock (Gate)
        {
            if (Steps.Count == 0)
                return;
            snapshot = [..Steps];
            Steps.Clear();
        }

        decimal gain = 0, lose = 0;
        var setCount = 0;
        foreach (var s in snapshot)
        {
            switch (s.Op)
            {
                case "gain":
                    gain += s.Amount;
                    break;
                case "lose":
                    lose += s.Amount;
                    break;
                case "set":
                    setCount++;
                    break;
            }
        }

        var scope = TelemetryScopeContext.Snapshot();
        var handSeq = TelemetryScopeContext.CurrentHandSequence;
        var turnPlayerKey = snapshot.Count > 0 ? snapshot[^1].PlayerKey : scope.LastEnergyPlayerKey;
        TelemetryEventLog.WriteRaw(
            "combat_player_energy_turn",
            new CombatPlayerEnergyTurnPayload(
                CombatOrdinal: scope.CombatOrdinal,
                HandSequence: handSeq,
                PlayerKey: turnPlayerKey,
                RoundWhenStarted: roundWhenStarted,
                RoundWhenEnded: roundWhenEnded,
                EndReason: endReason,
                SegmentEndUtc: endUtc,
                ClosedByHistoryEntry: closedByHistoryEntry,
                StepCount: snapshot.Count,
                TotalGain: gain,
                TotalLose: lose,
                SetOperationCount: setCount,
                Steps: snapshot),
            endUtc);
        TelemetryScopeContext.AdvanceHandSequence();
    }
}

public sealed record CombatEnergyFlowStepPayload(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("energyAfter")] int EnergyAfter,
    [property: JsonPropertyName("maxEnergyAfter")] int MaxEnergyAfter,
    [property: JsonPropertyName("utc")] DateTime Utc,
    [property: JsonPropertyName("playerKey")] string? PlayerKey);

public sealed record CombatPlayerEnergyTurnPayload(
    [property: JsonPropertyName("combatOrdinal")] int CombatOrdinal,
    [property: JsonPropertyName("handSequence")] int HandSequence,
    [property: JsonPropertyName("playerKey")] string? PlayerKey,
    [property: JsonPropertyName("roundWhenStarted")] string? RoundWhenStarted,
    [property: JsonPropertyName("roundWhenEnded")] string? RoundWhenEnded,
    [property: JsonPropertyName("endReason")] string EndReason,
    [property: JsonPropertyName("segmentEndUtc")] DateTime SegmentEndUtc,
    [property: JsonPropertyName("closedByHistoryEntry")] string? ClosedByHistoryEntry,
    [property: JsonPropertyName("stepCount")] int StepCount,
    [property: JsonPropertyName("totalGain")] decimal TotalGain,
    [property: JsonPropertyName("totalLose")] decimal TotalLose,
    [property: JsonPropertyName("setOperationCount")] int SetOperationCount,
    [property: JsonPropertyName("steps")] IReadOnlyList<CombatEnergyFlowStepPayload> Steps);
