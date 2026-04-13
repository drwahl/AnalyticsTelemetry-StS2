using System;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Shared chart math (must match <see cref="MetricsTimeSeriesRenderer"/>).</summary>
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

    /// <summary>Maximum Y across every point in every series on <em>this</em> chart (one shared vertical scale per graph).</summary>
    public static double ComputeSharedSeriesDataMax(IReadOnlyList<MetricTimeSeries> series)
    {
        var m = 0.0;
        foreach (var s in series)
        {
            if (s.Values.Count < 1)
                continue;
            foreach (var v in s.Values)
            {
                if (v > m)
                    m = v;
            }
        }

        return m;
    }

    /// <summary>Largest value in a single series’ value list.</summary>
    public static double ComputeSeriesWindowMax(IReadOnlyList<double> values)
    {
        if (values.Count < 1)
            return 0;
        var m = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > m)
                m = values[i];
        }

        return m;
    }

    /// <summary>Denominator for <c>y = value / denom</c> when mapping to <c>[0,1]</c>; flat series use 1 so the line stays visible at 0.</summary>
    public static double ChartNormalizeDenominator(double dataMax)
    {
        return dataMax < 1e-6 ? 1.0 : dataMax;
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
