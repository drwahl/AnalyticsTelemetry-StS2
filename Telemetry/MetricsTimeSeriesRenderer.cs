using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Renders simple multi-series line charts to an <see cref="ImageTexture"/> (no custom Control subclass).</summary>
internal static class MetricsTimeSeriesRenderer
{
    private const int DefaultWidth = 320;
    private const int DefaultHeight = 112;
    private const int MarginL = 4;
    private const int MarginR = 4;
    private const int MarginT = 6;
    private const int MarginB = 6;

    internal static int PlotMarginLeft => MarginL;
    internal static int PlotMarginRight => MarginR;
    internal static int PlotMarginTop => MarginT;
    internal static int PlotMarginBottom => MarginB;

    /// <summary>Builds a texture or returns null if there is nothing meaningful to draw.</summary>
    public static ImageTexture? TryBuildChart(
        IReadOnlyList<MetricTimeSeries> series,
        int width = DefaultWidth,
        int height = DefaultHeight,
        bool sleekVisuals = false,
        int lineThickness = 1)
    {
        if (series.Count == 0)
            return null;
        var maxRaw = 0;
        foreach (var s in series)
        {
            if (s.Values.Count > maxRaw)
                maxRaw = s.Values.Count;
        }

        if (maxRaw < 1)
            return null;
        // Need ≥2 X steps to draw a segment; duplicate a lone sample so early-session / single-bucket replay still shows a line.
        var n = maxRaw < 2 ? 2 : maxRaw;

        var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        img.Fill(sleekVisuals
            ? new Color(0.035f, 0.038f, 0.044f, 1f)
            : new Color(0.07f, 0.075f, 0.09f, 1f));

        var plotW = width - MarginL - MarginR;
        var plotH = height - MarginT - MarginB;
        if (plotW < 8 || plotH < 8)
            return null;

        var chartDataMax = MetricsTimeSeriesMath.ComputeSharedSeriesDataMax(series);
        var denom = MetricsTimeSeriesMath.ChartNormalizeDenominator(chartDataMax);

        DrawGrid(img, width, height, plotW, plotH, sleekVisuals);

        var thick = sleekVisuals ? Math.Max(1, lineThickness) : lineThickness;
        foreach (var s in series)
        {
            if (s.Values.Count < 1)
                continue;
            var pts = NormalizePoints(s.Values, n, plotW, plotH, denom);
            DrawPolyline(img, pts, s.Stroke, width, height, thick);
        }

        return ImageTexture.CreateFromImage(img);
    }

    private static void DrawGrid(Image img, int width, int height, int plotW, int plotH, bool sleek)
    {
        var g = sleek
            ? new Color(0.11f, 0.12f, 0.14f, 0.55f)
            : new Color(0.18f, 0.2f, 0.24f, 1f);
        for (var i = 0; i <= 4; i++)
        {
            var y = MarginT + (int)(plotH * (i / 4f));
            // Slightly stronger line at vertical middle (50% of plot) for orientation.
            var lineCol = i == 2
                ? (sleek ? new Color(0.16f, 0.17f, 0.2f, 0.75f) : new Color(0.28f, 0.31f, 0.36f, 1f))
                : g;
            HLine(img, MarginL, width - MarginR, y, lineCol);
        }

        for (var i = 0; i <= 6; i++)
        {
            var x = MarginL + (int)(plotW * (i / 6f));
            VLine(img, x, MarginT, height - MarginB, g);
        }
    }

    private static Vector2[] NormalizePoints(IReadOnlyList<double> values, int targetLen, int plotW, int plotH, double yDenom)
    {
        var pts = new Vector2[targetLen];
        for (var i = 0; i < targetLen; i++)
        {
            var v = i < values.Count ? values[i] : values[^1];
            var nx = targetLen <= 1 ? 0 : i / (float)(targetLen - 1);
            var ny = (float)Math.Clamp(v / yDenom, 0, 1);
            pts[i] = new Vector2(
                MarginL + nx * plotW,
                MarginT + plotH - ny * plotH);
        }

        return pts;
    }

    private static void DrawPolyline(Image img, Vector2[] pts, Color color, int w, int h, int thickness)
    {
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var x0 = (int)Math.Round(pts[i].X);
            var y0 = (int)Math.Round(pts[i].Y);
            var x1 = (int)Math.Round(pts[i + 1].X);
            var y1 = (int)Math.Round(pts[i + 1].Y);
            if (thickness <= 1)
                DrawLineBresenham(img, x0, y0, x1, y1, color, w, h);
            else
                DrawThickLine(img, pts[i], pts[i + 1], color, w, h, thickness);
        }
    }

    /// <summary>1px lines avoid the “beaded necklace” look from stamping thick disks every pixel along shallow segments.</summary>
    private static void DrawLineBresenham(Image img, int x0, int y0, int x1, int y1, Color c, int w, int h)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        while (true)
        {
            SetPx(img, x0, y0, c, w, h);
            if (x0 == x1 && y0 == y1)
                break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void DrawThickLine(Image img, Vector2 a, Vector2 b, Color c, int w, int h, int thickness)
    {
        var steps = (int)Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        steps = Math.Max(steps, 1);
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (float)steps;
            var x = (int)Math.Round(Mathf.Lerp(a.X, b.X, t));
            var y = (int)Math.Round(Mathf.Lerp(a.Y, b.Y, t));
            for (var dx = -thickness; dx <= thickness; dx++)
            {
                for (var dy = -thickness; dy <= thickness; dy++)
                {
                    if (dx * dx + dy * dy > thickness * thickness + 1)
                        continue;
                    SetPx(img, x + dx, y + dy, c, w, h);
                }
            }
        }
    }

    private static void HLine(Image img, int x0, int x1, int y, Color c)
    {
        for (var x = x0; x <= x1; x++)
            SetPx(img, x, y, c, img.GetWidth(), img.GetHeight());
    }

    private static void VLine(Image img, int x, int y0, int y1, Color c)
    {
        for (var y = y0; y <= y1; y++)
            SetPx(img, x, y, c, img.GetWidth(), img.GetHeight());
    }

    private static void SetPx(Image img, int x, int y, Color c, int w, int h)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h)
            return;
        img.SetPixel(x, y, c);
    }
}
