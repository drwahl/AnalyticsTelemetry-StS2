using System;
using System.Globalization;
using System.Text;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Transparent overlay on a chart texture: per-series Y at the vertical crosshair (matches shared chart scale),
/// optional “scale at cursor Y” ruler, sample index along X, and probe dots on each line.
/// </summary>
internal sealed partial class ChartHoverCatcher : Control
{
    private readonly IReadOnlyList<MetricTimeSeries> _series;
    private readonly int _marginL;
    private readonly int _marginR;
    private readonly int _marginT;
    private readonly int _marginB;
    private readonly float _plotH;
    private readonly int _n;
    private readonly Label _tip;
    private bool _hasHover;
    private float _crosshairX;
    private double _probeT;

    public ChartHoverCatcher(
        IReadOnlyList<MetricTimeSeries> series,
        int width,
        int height,
        int marginL,
        int marginR)
    {
        _series = series;
        _marginL = marginL;
        _marginR = marginR;
        _marginT = MetricsTimeSeriesRenderer.PlotMarginTop;
        _marginB = MetricsTimeSeriesRenderer.PlotMarginBottom;
        _plotH = height - _marginT - _marginB;
        CustomMinimumSize = new Vector2(width, height);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = false;

        _n = MetricsTimeSeriesMath.ChartSampleCount(series);

        _tip = new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = false,
        };
        _tip.AddThemeFontSizeOverride("font_size", 10);
        _tip.AddThemeColorOverride("font_color", new Color(0.94f, 0.95f, 0.9f, 1f));
        var tipBg = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.94f),
            BorderColor = new Color(0.35f, 0.4f, 0.48f, 0.85f),
        };
        tipBg.SetBorderWidthAll(1);
        tipBg.SetCornerRadiusAll(4);
        tipBg.ContentMarginLeft = tipBg.ContentMarginRight = 6;
        tipBg.ContentMarginTop = tipBg.ContentMarginBottom = 3;
        _tip.AddThemeStyleboxOverride("normal", tipBg);
        _tip.ZIndex = 10;
        AddChild(_tip);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm)
        {
            UpdateHover(mm.Position);
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
        {
            _hasHover = false;
            _tip.Visible = false;
            QueueRedraw();
        }
    }

    private void UpdateHover(Vector2 localPos)
    {
        var w = Size.X;
        if (w < 1f)
            return;
        var plotW = w - _marginL - _marginR;
        if (plotW < 4f)
            return;
        if (localPos.X < _marginL || localPos.X > w - _marginR)
        {
            _hasHover = false;
            _tip.Visible = false;
            QueueRedraw();
            return;
        }

        var x = localPos.X;
        var frac = (x - _marginL) / plotW;
        frac = Mathf.Clamp(frac, 0f, 1f);
        var t = _n <= 1 ? 0d : frac * (_n - 1);
        _probeT = t;
        _crosshairX = x;
        _hasHover = true;
        QueueRedraw();

        var sb = new StringBuilder(512);
        sb.Append("Y at this X (plotted height = value ÷ chart max):\n");

        var any = false;
        var seriesShown = 0;
        const int maxSeriesInTip = 18;
        foreach (var s in _series)
        {
            if (s.Values.Count < 1)
                continue;
            seriesShown++;
            if (seriesShown > maxSeriesInTip)
            {
                sb.Append("  • …\n");
                break;
            }

            any = true;
            sb.Append("  • ");
            sb.Append(ShortTitle(s.Title, 26));
            sb.Append(": ");
            var plotted = MetricsTimeSeriesMath.InterpolateAtChartIndex(s.Values, _n, t);
            if (s.SessionTotalAtSample is { Count: > 0 } tot)
            {
                var session = MetricsTimeSeriesMath.InterpolateAtChartIndex(tot, _n, t);
                sb.Append("Σ ");
                sb.Append(FormatHoverNumber(session));
                sb.Append(" · Δ ");
                sb.Append(FormatHoverNumber(plotted));
            }
            else
                sb.Append(FormatHoverNumber(plotted));

            sb.Append('\n');
        }

        if (!any)
            sb.Append("  —\n");

        sb.Append("Along X: sample index ");
        sb.Append(t.ToString("F2", CultureInfo.InvariantCulture));
        sb.Append(" / ");
        sb.Append((_n - 1).ToString(CultureInfo.InvariantCulture));

        if (_plotH > 1e-3f
            && localPos.Y >= _marginT
            && localPos.Y <= Size.Y - _marginB)
        {
            var dataMax = MetricsTimeSeriesMath.ComputeSharedSeriesDataMax(_series);
            var denom = MetricsTimeSeriesMath.ChartNormalizeDenominator(dataMax);
            var ny = (float)((localPos.Y - _marginT) / _plotH);
            ny = Mathf.Clamp(ny, 0f, 1f);
            var axisVal = denom * (1.0 - ny);
            sb.Append("\nShared Y scale at cursor row ≈ ");
            sb.Append(FormatHoverNumber(axisVal));
            sb.Append(" (0 at bottom, max at top of plot)");
        }

        _tip.Text = sb.ToString();
        _tip.Visible = true;
        var wrapW = Mathf.Clamp(Size.X - 12, 120, 320);
        _tip.CustomMinimumSize = new Vector2(wrapW, 0);
        var tipSize = _tip.GetMinimumSize();
        _tip.CustomMinimumSize = new Vector2(wrapW, tipSize.Y);
        var pos = localPos + new Vector2(8, 8);
        if (pos.X + tipSize.X > Size.X)
            pos.X = localPos.X - tipSize.X - 6;
        if (pos.Y + tipSize.Y > Size.Y)
            pos.Y = localPos.Y - tipSize.Y - 6;
        _tip.Position = pos;
    }

    private static string ShortTitle(string title, int maxChars)
    {
        if (string.IsNullOrEmpty(title))
            return "?";
        if (title.Length <= maxChars)
            return title;
        return string.Concat(title.AsSpan(0, maxChars - 1), "…");
    }

    private static string FormatHoverNumber(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return "—";
        var ax = Math.Abs(x);
        if (ax < 1e-12)
            return "0";
        if (Math.Abs(x - Math.Round(x)) < 1e-9 && ax < 2e15)
            return ((long)Math.Round(x)).ToString("N0", CultureInfo.InvariantCulture);
        return x.ToString("G5", CultureInfo.InvariantCulture);
    }

    public override void _Draw()
    {
        if (!_hasHover)
            return;
        var lineColor = new Color(0.9f, 0.9f, 0.95f, 0.35f);
        DrawLine(new Vector2(_crosshairX, 0), new Vector2(_crosshairX, Size.Y), lineColor, 1f, true);

        if (_plotH < 4f)
            return;
        var dataMax = MetricsTimeSeriesMath.ComputeSharedSeriesDataMax(_series);
        var denom = MetricsTimeSeriesMath.ChartNormalizeDenominator(dataMax);
        if (denom < 1e-15)
            return;
        var t = _probeT;
        foreach (var s in _series)
        {
            if (s.Values.Count < 1)
                continue;
            var v = MetricsTimeSeriesMath.InterpolateAtChartIndex(s.Values, _n, t);
            if (double.IsNaN(v) || double.IsInfinity(v))
                continue;
            var ny = Mathf.Clamp((float)(v / denom), 0f, 1f);
            var py = _marginT + _plotH - ny * _plotH;
            var c = s.Stroke;
            c.A = 0.92f;
            DrawCircle(new Vector2(_crosshairX, py), 3f, c);
        }
    }
}
