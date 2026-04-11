namespace AnalyticsTelemetry.Telemetry;

/// <summary>Presentation flags for <see cref="MetricsVisualPanelFactory"/> (overlay + mod settings).</summary>
internal readonly record struct MetricsVisualUiOptions(
    bool CompactCharts,
    bool SleekCharts,
    bool InteractiveHover)
{
    internal static MetricsVisualUiOptions Default { get; } = new(
        CompactCharts: false,
        SleekCharts: false,
        InteractiveHover: true);
}
