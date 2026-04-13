namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Wall time from map screen showing (player can choose the next node) until <see cref="MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.TravelToMapCoord"/> runs.
/// </summary>
internal static class MapPathDecisionTelemetry
{
    private const double MaxReasonableDecisionMs = 3_600_000d;

    private static DateTime? _decisionStartUtc;

    internal static void ResetForNewSession() => _decisionStartUtc = null;

    internal static void OnMapScreenVisibilityChanged(bool visible)
    {
        if (visible)
            _decisionStartUtc = DateTime.UtcNow;
        else
            _decisionStartUtc = null;
    }

    /// <summary>Called from Harmony prefix on <c>TravelToMapCoord</c> (commit / start of travel).</summary>
    internal static void OnTravelToMapCoordInvoked()
    {
        if (_decisionStartUtc is not { } start)
            return;
        var now = DateTime.UtcNow;
        var ms = (now - start).TotalMilliseconds;
        _decisionStartUtc = null;
        if (ms < 0 || ms > MaxReasonableDecisionMs)
            return;

        var s = TelemetryScopeContext.Snapshot();
        TelemetryEventLog.WriteRaw(
            "map_path_decision",
            new MapPathDecisionPayload(
                DecisionMs: ms,
                ActIndex: s.ActIndex,
                ActId: s.ActId,
                MapDepth: s.MapDepth,
                CombatOrdinal: s.CombatOrdinal,
                RunMode: s.RunMode));
    }
}
