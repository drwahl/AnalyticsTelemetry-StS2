using System;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Shared chart X-axis math (must match <see cref="MetricsTimeSeriesRenderer"/> sample count rules).</summary>
internal static class MetricsTimeSeriesMath
{
    /// <summary>Same <c>n</c> as the renderer: max raw length, at least 2.</summary>
    public static int ChartSampleCount(IReadOnlyList<MetricTimeSeries> series)
    {
        var maxRaw = 0;
        foreach (var s in series)
            maxRaw = Math.Max(maxRaw, s.Values.Count);
        return maxRaw < 2 ? 2 : maxRaw;
    }

    /// <summary>Value at fractional chart index <paramref name="t"/> in <c>[0, n-1]</c>, linearly interpolated between integer steps (matches polyline).</summary>
    public static double InterpolateAtChartIndex(IReadOnlyList<double> values, int n, double t)
    {
        if (values.Count < 1 || n < 1)
            return double.NaN;
        if (n == 1)
            return ValueAtIntegerIndex(values, n, 0);
        t = Math.Clamp(t, 0, n - 1);
        var i0 = (int)Math.Floor(t);
        var i1 = Math.Min(i0 + 1, n - 1);
        var f = t - i0;
        var v0 = ValueAtIntegerIndex(values, n, i0);
        var v1 = ValueAtIntegerIndex(values, n, i1);
        return v0 + (v1 - v0) * f;
    }

    private static double ValueAtIntegerIndex(IReadOnlyList<double> values, int n, int i)
    {
        i = Math.Clamp(i, 0, n - 1);
        var vi = i < values.Count ? values[i] : values[^1];
        return vi;
    }
}
