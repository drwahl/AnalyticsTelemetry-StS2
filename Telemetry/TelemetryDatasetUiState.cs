namespace AnalyticsTelemetry.Telemetry;

/// <summary>Shared dataset choice for the in-game overlay and Settings → Mods live tab.</summary>
internal static class TelemetryDatasetUiState
{
    public static TelemetryDatasetSelection Selection { get; set; } = TelemetryDatasetSelection.Current;
}
