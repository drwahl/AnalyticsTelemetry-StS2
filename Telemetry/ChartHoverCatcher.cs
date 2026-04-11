using System;
using System.Globalization;
using System.Text;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Transparent overlay on a chart texture: hover shows interpolated values along X (matches polyline),
/// optional session totals, and a crosshair at the pointer.
/// </summary>
internal sealed partial class ChartHoverCatcher : Control
{
    private readonly IReadOnlyList<MetricTimeSeries> _series;
    private readonly int _marginL;
    private readonly int _marginR;
    private readonly int _n;
    private readonly string? _hoverFootnote;
    private readonly Label _tip;
    private bool _hasHover;
    private float _crosshairX;

    public ChartHoverCatcher(
        IReadOnlyList<MetricTimeSeries> series,
        int width,
        int height,
        int marginL,
        int marginR,
        string? hoverFootnote = null)
    {
        _series = series;
        _marginL = marginL;
        _marginR = marginR;
        _hoverFootnote = hoverFootnote;
        CustomMinimumSize = new Vector2(width, height);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = false;

        _n = MetricsTimeSeriesMath.ChartSampleCount(series);

        _tip = new Label
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _tip.AddThemeFontSizeOverride("font_size", 11);
        _tip.AddThemeColorOverride("font_color", new Color(0.94f, 0.95f, 0.9f, 1f));
        var tipBg = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.94f),
            BorderColor = new Color(0.35f, 0.4f, 0.48f, 0.85f),
        };
        tipBg.SetBorderWidthAll(1);
        tipBg.SetCornerRadiusAll(4);
        tipBg.ContentMarginLeft = tipBg.ContentMarginRight = 8;
        tipBg.ContentMarginTop = tipBg.ContentMarginBottom = 5;
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
        _crosshairX = x;
        _hasHover = true;
        QueueRedraw();

        var sb = new StringBuilder(320);
        if (!string.IsNullOrWhiteSpace(_hoverFootnote))
        {
            sb.Append(_hoverFootnote.Trim());
            sb.Append("\n\n");
        }

        sb.Append("Along X: index ").Append(t.ToString("F2", CultureInfo.InvariantCulture));
        sb.Append(" (of ").Append(_n).Append(" points)\n");
        foreach (var s in _series)
        {
            if (s.Values.Count < 1)
                continue;
            AppendSeriesHoverLine(sb, s, t, _n);
        }

        _tip.Text = sb.ToString().TrimEnd();
        _tip.Visible = true;
        var tipSize = _tip.GetMinimumSize();
        var pos = localPos + new Vector2(10, 10);
        if (pos.X + tipSize.X > Size.X)
            pos.X = localPos.X - tipSize.X - 8;
        if (pos.Y + tipSize.Y > Size.Y)
            pos.Y = localPos.Y - tipSize.Y - 8;
        _tip.Position = pos;
        _tip.CustomMinimumSize = new Vector2(Mathf.Min(280, Size.X - 16), 0);
    }

    private static void AppendSeriesHoverLine(StringBuilder sb, MetricTimeSeries s, double t, int n)
    {
        var plotted = MetricsTimeSeriesMath.InterpolateAtChartIndex(s.Values, n, t);
        if (s.SessionTotalAtSample is { Count: > 0 } tot)
        {
            var session = MetricsTimeSeriesMath.InterpolateAtChartIndex(tot, n, t);
            sb.Append(s.Title).Append(": ");
            sb.Append(FormatHoverNumber(session)).Append(" session total");
            if (Math.Abs(plotted) > 1e-12 || Math.Abs(session) > 1e-12)
                sb.Append(" · ").Append(FormatHoverNumber(plotted)).Append(" change (interp.)");
            sb.Append('\n');
        }
        else
            sb.Append(s.Title).Append(": ").Append(FormatHoverNumber(plotted)).Append('\n');
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
    }
}
