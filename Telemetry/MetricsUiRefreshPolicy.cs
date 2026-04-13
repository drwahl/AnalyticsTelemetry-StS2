namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Pure decision for metrics pane updates. Keeps “chart texture rebuild” (very expensive) separate from
/// volatile UI refresh. Regression tests lock this behavior — full rebuilds every tick tank FPS.
/// </summary>
internal enum MetricsUiRefreshKind
{
    /// <summary>Nothing to apply; signatures match and scroll-to-top not forced.</summary>
    Noop,

    /// <summary>
    /// <see cref="MetricsVisualPanelFactory.Rebuild"/> — recreates chart textures and full tree.
    /// Must not run on counter/header-only churn.
    /// </summary>
    FullRebuild,

    /// <summary>
    /// <see cref="MetricsVisualPanelFactory.TryRefreshVolatileOnly"/> — headers + tail only.
    /// </summary>
    VolatileOnly,
}

internal static class MetricsUiRefreshPolicy
{
    /// <summary>
    /// Minimum wall time between chart texture redraws when only <paramref name="chartDataSignature"/> drifts
    /// (live samples updating). Layout/drill changes bypass this. ~3s batches raster work; volatile UI can still update faster.
    /// </summary>
    public const ulong ChartDataTextureMinIntervalMsec = 3000;

    /// <summary>
    /// When <see cref="DecideWithThrottledChartTextures"/> returns <see cref="MetricsUiRefreshKind.FullRebuild"/> because
    /// chart <i>data</i> changed, this tells the host whether it may call
    /// <see cref="MetricsVisualPanelFactory.TrySwapTimeSeriesChartTextures"/> instead of
    /// <see cref="MetricsVisualPanelFactory.Rebuild"/>. In-place swaps avoid freeing the whole metrics subtree (major FPS win).
    /// </summary>
    public static bool ShouldUseInPlaceChartTextureSwap(
        bool scrollToTop,
        string chartLayoutSignature,
        string lastChartLayoutSignature,
        string chartDataSignature,
        string lastChartDataSignature,
        string volatileSignature,
        string lastVolatileSignature) =>
        !scrollToTop
        && chartLayoutSignature == lastChartLayoutSignature
        && chartDataSignature != lastChartDataSignature
        && volatileSignature == lastVolatileSignature;

    /// <param name="scrollToTop">When true, forces <see cref="MetricsUiRefreshKind.FullRebuild"/> (overlay / settings UX).</param>
    public static MetricsUiRefreshKind Decide(
        string chartSignature,
        string volatileSignature,
        string lastChartSignature,
        string lastVolatileSignature,
        bool scrollToTop)
    {
        if (chartSignature == lastChartSignature
            && volatileSignature == lastVolatileSignature
            && !scrollToTop)
            return MetricsUiRefreshKind.Noop;

        if (scrollToTop || chartSignature != lastChartSignature)
            return MetricsUiRefreshKind.FullRebuild;

        if (volatileSignature != lastVolatileSignature)
            return MetricsUiRefreshKind.VolatileOnly;

        return MetricsUiRefreshKind.Noop;
    }

    /// <summary>
    /// Like <see cref="Decide"/> but throttles <see cref="MetricsUiRefreshKind.FullRebuild"/> when only chart
    /// <i>data</i> (not <paramref name="chartLayoutSignature"/>) changes. Updates <paramref name="nextChartTextureEligibleTicksMsec"/>.
    /// </summary>
    public static MetricsUiRefreshKind DecideWithThrottledChartTextures(
        string chartDataSignature,
        string chartLayoutSignature,
        string volatileSignature,
        string lastChartDataSignature,
        string lastChartLayoutSignature,
        string lastVolatileSignature,
        bool scrollToTop,
        ulong nowTicksMsec,
        ref ulong nextChartTextureEligibleTicksMsec)
    {
        if (scrollToTop)
        {
            nextChartTextureEligibleTicksMsec = nowTicksMsec + ChartDataTextureMinIntervalMsec;
            return MetricsUiRefreshKind.FullRebuild;
        }

        if (chartLayoutSignature != lastChartLayoutSignature)
        {
            nextChartTextureEligibleTicksMsec = nowTicksMsec + ChartDataTextureMinIntervalMsec;
            return MetricsUiRefreshKind.FullRebuild;
        }

        if (chartDataSignature != lastChartDataSignature)
        {
            if (nowTicksMsec >= nextChartTextureEligibleTicksMsec)
            {
                nextChartTextureEligibleTicksMsec = nowTicksMsec + ChartDataTextureMinIntervalMsec;
                return MetricsUiRefreshKind.FullRebuild;
            }

            if (volatileSignature != lastVolatileSignature)
                return MetricsUiRefreshKind.VolatileOnly;

            return MetricsUiRefreshKind.Noop;
        }

        if (volatileSignature != lastVolatileSignature)
            return MetricsUiRefreshKind.VolatileOnly;

        return MetricsUiRefreshKind.Noop;
    }
}
