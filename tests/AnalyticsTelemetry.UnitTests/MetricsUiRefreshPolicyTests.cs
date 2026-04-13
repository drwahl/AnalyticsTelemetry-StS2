using AnalyticsTelemetry.Telemetry;
using Godot;
using Xunit;

namespace AnalyticsTelemetry.UnitTests;

/// <summary>
/// Guards against FPS regressions when the analytics overlay is open: full <see cref="MetricsVisualPanelFactory.Rebuild"/>
/// (chart texture rasterization) must not run every tick when only counters / headers / bars change.
/// </summary>
public sealed class MetricsUiRefreshPolicyTests
{
    [Fact]
    public void Decide_VolatileOnly_WhenChartMatches_AndVolatileDiffers_AndNotScrollToTop()
    {
        var kind = MetricsUiRefreshPolicy.Decide(
            chartSignature: "chart|ui",
            volatileSignature: "volatileB",
            lastChartSignature: "chart|ui",
            lastVolatileSignature: "volatileA",
            scrollToTop: false);
        Assert.Equal(MetricsUiRefreshKind.VolatileOnly, kind);
    }

    [Fact]
    public void Decide_NoOp_WhenBothSignaturesMatch_AndNotScrollToTop()
    {
        var kind = MetricsUiRefreshPolicy.Decide("c|u", "v", "c|u", "v", scrollToTop: false);
        Assert.Equal(MetricsUiRefreshKind.Noop, kind);
    }

    [Fact]
    public void Decide_FullRebuild_WhenChartSignatureChanges_EvenIfVolatileMatches()
    {
        var kind = MetricsUiRefreshPolicy.Decide(
            chartSignature: "chartNEW|ui",
            volatileSignature: "sameVolatile",
            lastChartSignature: "chartOLD|ui",
            lastVolatileSignature: "sameVolatile",
            scrollToTop: false);
        Assert.Equal(MetricsUiRefreshKind.FullRebuild, kind);
    }

    [Fact]
    public void Decide_FullRebuild_WhenScrollToTop_EvenIfSignaturesUnchanged()
    {
        var kind = MetricsUiRefreshPolicy.Decide("c|u", "v", "c|u", "v", scrollToTop: true);
        Assert.Equal(MetricsUiRefreshKind.FullRebuild, kind);
    }

    [Fact]
    public void Regression_ChangeSignature_must_not_drive_policy_as_single_blob()
    {
        // If someone wires Decide() to model.ChangeSignature() only, every counter tick forces FullRebuild.
        var m = MetricsVisualModelSignatureTests.Mk(
            counters: new[] { new MetricCounter("Events", "1") },
            timeSeriesCharts: new[]
            {
                new MetricTimeSeriesChart(
                    "T",
                    new[] { new MetricTimeSeries("L", Colors.White, new[] { 1.0, 2.0 }) }),
            });
        var m2 = MetricsVisualModelSignatureTests.Mk(
            counters: new[] { new MetricCounter("Events", "999") },
            timeSeriesCharts: new[]
            {
                new MetricTimeSeriesChart(
                    "T",
                    new[] { new MetricTimeSeries("L", Colors.White, new[] { 1.0, 2.0 }) }),
            });
        var ui = "prefs";
        var chartA = m.ChartTextureSignature() + "|" + ui;
        var chartB = m2.ChartTextureSignature() + "|" + ui;
        Assert.Equal(chartA, chartB);

        var volA = m.VolatileUiSignature(true);
        var volB = m2.VolatileUiSignature(true);
        Assert.NotEqual(volA, volB);

        var kind = MetricsUiRefreshPolicy.Decide(chartA, volB, chartA, volA, scrollToTop: false);
        Assert.Equal(MetricsUiRefreshKind.VolatileOnly, kind);

        var badMonolithic = m.ChangeSignature(true);
        var badMonolithic2 = m2.ChangeSignature(true);
        Assert.NotEqual(badMonolithic, badMonolithic2);
    }

    [Fact]
    public void DecideWithThrottledChartTextures_NoOp_when_only_data_drifts_before_eligible()
    {
        ulong next = 10_000;
        var kind = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
            chartDataSignature: "dataB",
            chartLayoutSignature: "layout",
            volatileSignature: "vol",
            lastChartDataSignature: "dataA",
            lastChartLayoutSignature: "layout",
            lastVolatileSignature: "vol",
            scrollToTop: false,
            nowTicksMsec: 5000,
            ref next);
        Assert.Equal(MetricsUiRefreshKind.Noop, kind);
        Assert.Equal(10_000u, next);
    }

    [Fact]
    public void DecideWithThrottledChartTextures_VolatileOnly_when_data_drifts_but_counters_changed()
    {
        ulong next = 10_000;
        var kind = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
            chartDataSignature: "dataB",
            chartLayoutSignature: "layout",
            volatileSignature: "volNew",
            lastChartDataSignature: "dataA",
            lastChartLayoutSignature: "layout",
            lastVolatileSignature: "volOld",
            scrollToTop: false,
            nowTicksMsec: 5000,
            ref next);
        Assert.Equal(MetricsUiRefreshKind.VolatileOnly, kind);
        Assert.Equal(10_000u, next);
    }

    [Fact]
    public void DecideWithThrottledChartTextures_FullRebuild_when_data_drifts_and_past_eligible()
    {
        ulong next = 1000;
        var kind = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
            chartDataSignature: "dataB",
            chartLayoutSignature: "layout",
            volatileSignature: "vol",
            lastChartDataSignature: "dataA",
            lastChartLayoutSignature: "layout",
            lastVolatileSignature: "vol",
            scrollToTop: false,
            nowTicksMsec: 5000,
            ref next);
        Assert.Equal(MetricsUiRefreshKind.FullRebuild, kind);
        Assert.Equal(5000u + MetricsUiRefreshPolicy.ChartDataTextureMinIntervalMsec, next);
    }

    [Fact]
    public void DecideWithThrottledChartTextures_FullRebuild_immediately_on_layout_change()
    {
        ulong next = 99_999;
        var kind = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
            chartDataSignature: "data",
            chartLayoutSignature: "layoutB",
            volatileSignature: "vol",
            lastChartDataSignature: "data",
            lastChartLayoutSignature: "layoutA",
            lastVolatileSignature: "vol",
            scrollToTop: false,
            nowTicksMsec: 100,
            ref next);
        Assert.Equal(MetricsUiRefreshKind.FullRebuild, kind);
        Assert.Equal(100u + MetricsUiRefreshPolicy.ChartDataTextureMinIntervalMsec, next);
    }

    /// <summary>
    /// <see cref="MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap"/> must stay aligned with overlay / mod config:
    /// only then do we call <c>TrySwapTimeSeriesChartTextures</c> instead of <c>Rebuild</c>.
    /// </summary>
    public sealed class InPlaceChartTextureSwapPredicateTests
    {
        [Fact]
        public void True_when_only_chart_data_drifts_same_layout_same_volatile_not_scroll()
        {
            Assert.True(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: false,
                chartLayoutSignature: "layout|ui",
                lastChartLayoutSignature: "layout|ui",
                chartDataSignature: "dataB|ui",
                lastChartDataSignature: "dataA|ui",
                volatileSignature: "vol",
                lastVolatileSignature: "vol"));
        }

        [Fact]
        public void False_when_scroll_to_top()
        {
            Assert.False(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: true,
                chartLayoutSignature: "layout|ui",
                lastChartLayoutSignature: "layout|ui",
                chartDataSignature: "dataB|ui",
                lastChartDataSignature: "dataA|ui",
                volatileSignature: "vol",
                lastVolatileSignature: "vol"));
        }

        [Fact]
        public void False_when_layout_changed()
        {
            Assert.False(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: false,
                chartLayoutSignature: "layoutB|ui",
                lastChartLayoutSignature: "layoutA|ui",
                chartDataSignature: "dataB|ui",
                lastChartDataSignature: "dataA|ui",
                volatileSignature: "vol",
                lastVolatileSignature: "vol"));
        }

        [Fact]
        public void False_when_volatile_changed()
        {
            Assert.False(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: false,
                chartLayoutSignature: "layout|ui",
                lastChartLayoutSignature: "layout|ui",
                chartDataSignature: "dataB|ui",
                lastChartDataSignature: "dataA|ui",
                volatileSignature: "volNew",
                lastVolatileSignature: "volOld"));
        }

        [Fact]
        public void False_when_chart_data_unchanged()
        {
            Assert.False(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: false,
                chartLayoutSignature: "layout|ui",
                lastChartLayoutSignature: "layout|ui",
                chartDataSignature: "data|ui",
                lastChartDataSignature: "data|ui",
                volatileSignature: "vol",
                lastVolatileSignature: "vol"));
        }

        [Fact]
        public void Regression_FullRebuild_from_throttle_plus_predicate_implies_swap_not_nuclear_rebuild()
        {
            ulong next = 0;
            var plan = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
                chartDataSignature: "dataB|ui",
                chartLayoutSignature: "layout|ui",
                volatileSignature: "vol",
                lastChartDataSignature: "dataA|ui",
                lastChartLayoutSignature: "layout|ui",
                lastVolatileSignature: "vol",
                scrollToTop: false,
                nowTicksMsec: 10_000,
                ref next);
            Assert.Equal(MetricsUiRefreshKind.FullRebuild, plan);

            Assert.True(MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop: false,
                chartLayoutSignature: "layout|ui",
                lastChartLayoutSignature: "layout|ui",
                chartDataSignature: "dataB|ui",
                lastChartDataSignature: "dataA|ui",
                volatileSignature: "vol",
                lastVolatileSignature: "vol"));
        }
    }
}
