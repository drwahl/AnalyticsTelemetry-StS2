namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// When <see cref="MetricsDrillView.Act"/> is selected, optionally pin a specific act bucket instead of
/// following the live map act. Shared by overlay + mod settings.
/// </summary>
internal static class TelemetryActUiState
{
    /// <summary>null = follow live <see cref="TelemetryScopeContext"/> act; otherwise a key like <c>0:ActId</c>.</summary>
    public static string? SelectedActKey { get; set; }
}
