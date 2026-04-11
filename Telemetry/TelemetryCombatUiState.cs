namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// When <see cref="MetricsDrillView.Combat"/> is selected, optionally pin a specific combat ordinal instead of
/// following the live fight. Shared by overlay + mod settings.
/// </summary>
internal static class TelemetryCombatUiState
{
    /// <summary>null = follow live <see cref="TelemetryScopeContext"/> combat #; otherwise pin that ordinal.</summary>
    public static int? SelectedCombatOrdinal { get; set; }
}
