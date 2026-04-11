using System.Globalization;
using System.Text;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Structured metrics for chart/counter UI (overlay + mod settings).</summary>
internal sealed class MetricsVisualModel
{
    public required string ViewTitle { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    /// <summary>NDJSON / history / run-save volumes (always populated for chart visibility).</summary>
    public IReadOnlyList<MetricBar> RecordingBars { get; init; } = Array.Empty<MetricBar>();
    /// <summary>Combat card-flow categories (plays/draws/…); zeros included so the chart is never empty.</summary>
    public IReadOnlyList<MetricBar> CardFlowBars { get; init; } = Array.Empty<MetricBar>();
    /// <summary>Visited map room types from run save (<c>map_point_history</c>), when available.</summary>
    public IReadOnlyList<MetricBar> RoomVisitBars { get; init; } = Array.Empty<MetricBar>();
    /// <summary>One texture per group (e.g. throughput vs damage vs energy).</summary>
    public IReadOnlyList<MetricTimeSeriesChart> TimeSeriesCharts { get; init; } = Array.Empty<MetricTimeSeriesChart>();
    /// <summary>When true and a chart exists, bar sections that duplicate the same totals are de-emphasized.</summary>
    public bool PreferTimeSeriesOverBars { get; init; }
    public IReadOnlyList<MetricCounter> Counters { get; init; } = Array.Empty<MetricCounter>();
    /// <summary>Top cards by damage attributed from play brackets (see overlay caption).</summary>
    public IReadOnlyList<MetricCounter> CardDamageLeaders { get; init; } = Array.Empty<MetricCounter>();
    /// <summary>Top powers / card afflictions by inferred recipient (combat history).</summary>
    public IReadOnlyList<MetricCounter> StatusEffectLeaders { get; init; } = Array.Empty<MetricCounter>();
    public IReadOnlyList<HandBarPoint> HandBars { get; init; } = Array.Empty<HandBarPoint>();
    public string DetailText { get; init; } = "";

    /// <summary>Cheap fingerprint so UI can skip rebuilding when nothing changed.</summary>
    /// <param name="includeDetailText">When false, detail text changes do not invalidate (overlay compact mode).</param>
    public string ChangeSignature(bool includeDetailText = true) =>
        ChartTextureSignature() + "##V##" + VolatileUiSignature(includeDetailText);

    /// <summary>
    /// Fingerprint for <b>expensive</b> chart texture rebuilds (multi-series rasterization). Excludes counters and
    /// volume bars that change every NDJSON line — those use <see cref="VolatileUiSignature"/>.
    /// </summary>
    public string ChartTextureSignature()
    {
        var sb = new StringBuilder(256);
        sb.Append(ViewTitle);
        sb.Append('|').Append('P').Append(PreferTimeSeriesOverBars ? '1' : '0');
        foreach (var chart in TimeSeriesCharts)
        {
            sb.Append("|TS|").Append(chart.SectionTitle);
            foreach (var ts in chart.Series)
                AppendTimeSeriesFingerprint(sb, ts);
        }

        return sb.ToString();
    }

    /// <summary>Headers, bars, grids, and detail — cheap to refresh without redrawing chart textures.</summary>
    public string VolatileUiSignature(bool includeDetailText)
    {
        var sb = new StringBuilder(512);
        foreach (var h in Headers)
            sb.Append("|h|").Append(h);
        foreach (var b in RecordingBars)
            sb.Append('|').Append('R').Append(b.Label).Append('=').Append(b.Value);
        foreach (var b in CardFlowBars)
            sb.Append('|').Append('C').Append(b.Label).Append('=').Append(b.Value);
        foreach (var b in RoomVisitBars)
            sb.Append('|').Append('M').Append(b.Label).Append('=').Append(b.Value);
        foreach (var c in Counters)
            sb.Append('|').Append(c.Label).Append('=').Append(c.Value);
        foreach (var c in CardDamageLeaders)
            sb.Append("|K").Append(c.Label).Append('=').Append(c.Value);
        foreach (var c in StatusEffectLeaders)
            sb.Append("|S").Append(c.Label).Append('=').Append(c.Value);
        foreach (var p in HandBars)
            sb.Append('|').Append(p.Steps).Append('@').Append(p.CombatOrdinal).Append('.').Append(p.HandSequence);
        if (includeDetailText)
            sb.Append("##").Append(DetailText);
        else
            sb.Append("##~");
        return sb.ToString();
    }

    /// <summary>
    /// O(1) per series — avoids hashing hundreds of doubles every frame (overlay calls
    /// <see cref="ChangeSignature"/> on each <c>ProcessFrame</c>). Endpoints catch append/trim sliding windows.
    /// </summary>
    private static void AppendTimeSeriesFingerprint(StringBuilder sb, in MetricTimeSeries ts)
    {
        sb.Append('|').Append('T').Append(ts.Title).Append(':').Append(ts.Values.Count);
        if (ts.Values.Count > 0)
        {
            sb.Append(':').Append(ts.Values[0].ToString("G4", CultureInfo.InvariantCulture));
            sb.Append(':').Append(ts.Values[^1].ToString("G4", CultureInfo.InvariantCulture));
        }

        var snap = ts.SessionTotalAtSample;
        if (snap is null || snap.Count == 0)
            return;
        sb.Append(":snap").Append(snap.Count);
        sb.Append(':').Append(snap[0].ToString("G4", CultureInfo.InvariantCulture));
        sb.Append(':').Append(snap[^1].ToString("G4", CultureInfo.InvariantCulture));
    }
}

internal readonly record struct MetricBar(string Label, double Value, Color Fill);

/// <summary>Named group of lines sharing one chart image (shared X = sample index or time bucket).</summary>
internal readonly record struct MetricTimeSeriesChart(
    string SectionTitle,
    IReadOnlyList<MetricTimeSeries> Series,
    /// <summary>Optional one-line hint for hover (e.g. live vs replay semantics).</summary>
    string? HoverFootnote = null);

/// <summary>One line in a multi-series chart (shared X = sample index or time bucket).</summary>
/// <param name="SessionTotalAtSample">When set (live charts), hover shows running session total at that sample plus the plotted Δ.</param>
internal readonly record struct MetricTimeSeries(
    string Title,
    Color Stroke,
    IReadOnlyList<double> Values,
    IReadOnlyList<double>? SessionTotalAtSample = null);

internal readonly record struct MetricCounter(string Label, string Value);

/// <summary>One column in the hands mini-chart (combat, hand index, steps, player).</summary>
internal readonly record struct HandBarPoint(int CombatOrdinal, int HandSequence, int Steps, string? PlayerKey);
