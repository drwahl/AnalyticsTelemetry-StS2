using AnalyticsTelemetry.Telemetry;
using Godot;
using Xunit;

namespace AnalyticsTelemetry.UnitTests;

public sealed class MetricsVisualModelSignatureTests
{
    internal static MetricsVisualModel Mk(
        string viewTitle = "Overview",
        IReadOnlyList<string>? headers = null,
        IReadOnlyList<MetricBar>? recordingBars = null,
        IReadOnlyList<MetricBar>? cardFlowBars = null,
        IReadOnlyList<MetricBar>? roomVisitBars = null,
        IReadOnlyList<MetricBar>? damageByCombatBars = null,
        IReadOnlyList<MetricTimeSeriesChart>? timeSeriesCharts = null,
        bool preferTimeSeriesOverBars = false,
        IReadOnlyList<MetricCounter>? counters = null,
        IReadOnlyList<MetricCounter>? cardDamageLeaders = null,
        IReadOnlyList<MetricCounter>? statusEffectLeaders = null,
        IReadOnlyList<HandBarPoint>? handBars = null,
        string detailText = "") =>
        new()
        {
            ViewTitle = viewTitle,
            Headers = headers ?? Array.Empty<string>(),
            RecordingBars = recordingBars ?? Array.Empty<MetricBar>(),
            CardFlowBars = cardFlowBars ?? Array.Empty<MetricBar>(),
            RoomVisitBars = roomVisitBars ?? Array.Empty<MetricBar>(),
            DamageByCombatBars = damageByCombatBars ?? Array.Empty<MetricBar>(),
            TimeSeriesCharts = timeSeriesCharts ?? Array.Empty<MetricTimeSeriesChart>(),
            PreferTimeSeriesOverBars = preferTimeSeriesOverBars,
            Counters = counters ?? Array.Empty<MetricCounter>(),
            CardDamageLeaders = cardDamageLeaders ?? Array.Empty<MetricCounter>(),
            StatusEffectLeaders = statusEffectLeaders ?? Array.Empty<MetricCounter>(),
            HandBars = handBars ?? Array.Empty<HandBarPoint>(),
            DetailText = detailText,
        };

    [Fact]
    public void ChartTextureSignature_ignores_counter_and_header_churn()
    {
        var a = Mk(
            counters: new[] { new MetricCounter("Events", "99") },
            headers: new[] { "Combat 1s" });
        var b = Mk(
            counters: new[] { new MetricCounter("Events", "100") },
            headers: new[] { "Combat 2s" });
        Assert.Equal(a.ChartTextureSignature(), b.ChartTextureSignature());
    }

    [Fact]
    public void VolatileUiSignature_reflects_counters_and_headers()
    {
        var a = Mk(headers: new[] { "A" }, counters: new[] { new MetricCounter("Events", "1") });
        var b = Mk(headers: new[] { "B" }, counters: new[] { new MetricCounter("Events", "1") });
        var c = Mk(headers: new[] { "A" }, counters: new[] { new MetricCounter("Events", "2") });
        Assert.NotEqual(a.VolatileUiSignature(true), b.VolatileUiSignature(true));
        Assert.NotEqual(a.VolatileUiSignature(true), c.VolatileUiSignature(true));
    }

    [Fact]
    public void VolatileUiSignature_reflects_damage_by_combat_bars()
    {
        var lime = new Color(0.45f, 0.88f, 0.58f);
        var a = Mk(damageByCombatBars: new[] { new MetricBar("Combat #1", 95, lime) });
        var b = Mk(damageByCombatBars: new[] { new MetricBar("Combat #1", 110, lime) });
        Assert.NotEqual(a.VolatileUiSignature(true), b.VolatileUiSignature(true));
    }

    [Fact]
    public void VolatileUiSignature_detail_toggle_excludes_detail_text_when_false()
    {
        var a = Mk(detailText: "long\nscroll\nblock");
        var b = Mk(detailText: "different");
        Assert.Equal(a.VolatileUiSignature(false), b.VolatileUiSignature(false));
        Assert.NotEqual(a.VolatileUiSignature(true), b.VolatileUiSignature(true));
    }

    [Fact]
    public void ChangeSignature_composes_chart_and_volatile()
    {
        var m = Mk(
            timeSeriesCharts: new[]
            {
                new MetricTimeSeriesChart(
                    "Throughput",
                    new[]
                    {
                        new MetricTimeSeries("Line", Colors.White, new[] { 1.0, 2.0 }),
                    }),
            },
            counters: new[] { new MetricCounter("X", "1") });
        var expected = m.ChartTextureSignature() + "##V##" + m.VolatileUiSignature(true);
        Assert.Equal(expected, m.ChangeSignature(true));
    }

    [Fact]
    public void ChartLayoutSignature_stable_when_only_series_values_change()
    {
        var a = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "T",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0, 2.0 }) }),
        });
        var b = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "T",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0, 99.0 }) }),
        });
        Assert.Equal(a.ChartLayoutSignature(), b.ChartLayoutSignature());
    }

    [Fact]
    public void ChartLayoutSignature_changes_when_section_title_changes()
    {
        var a = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "A",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0 }) }),
        });
        var b = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "B",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0 }) }),
        });
        Assert.NotEqual(a.ChartLayoutSignature(), b.ChartLayoutSignature());
    }

    [Fact]
    public void ChartTextureSignature_changes_when_series_endpoints_change()
    {
        var a = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "T",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0, 2.0 }) }),
        });
        var b = Mk(timeSeriesCharts: new[]
        {
            new MetricTimeSeriesChart(
                "T",
                new[] { new MetricTimeSeries("L", Colors.Cyan, new[] { 1.0, 3.0 }) }),
        });
        Assert.NotEqual(a.ChartTextureSignature(), b.ChartTextureSignature());
    }
}
