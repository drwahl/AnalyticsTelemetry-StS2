using System.Linq;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Builds chart + counter UI from <see cref="MetricsVisualModel"/> into a host <see cref="Control"/>.</summary>
internal static class MetricsVisualPanelFactory
{
    internal const string NodeHeadersHost = "TelemetryHeadersHost";
    internal const string NodeChartsHost = "TelemetryChartsHost";
    internal const string NodeVolatileHost = "TelemetryVolatileHost";

    private static readonly Color CaptionColor = new(0.9f, 0.84f, 0.55f);
    private static readonly Color MutedColor = new(0.82f, 0.84f, 0.78f);
    private static readonly Color BarTrackColor = new(0.12f, 0.13f, 0.15f, 1f);
    private static readonly Color ValueColor = new(0.96f, 0.97f, 0.92f);

    /// <param name="compactDetail">When true (in-run overlay), omit the large duplicate text block at the bottom.</param>
    public static void Rebuild(
        Control host,
        MetricsVisualModel model,
        bool compactDetail = false,
        MetricsVisualUiOptions? presentation = null)
    {
        var pres = presentation ?? TelemetryMetricsUiPreferences.PresentationOptions;
        foreach (var child in host.GetChildren())
            child.Free();

        var root = new VBoxContainer { Name = "MetricsVisualRoot" };
        root.AddThemeConstantOverride("separation", compactDetail ? 5 : (pres.SleekCharts ? 6 : 7));
        host.AddChild(root);

        var title = new Label { Text = model.ViewTitle };
        title.AddThemeFontSizeOverride("font_size", compactDetail ? 16 : 18);
        title.AddThemeColorOverride("font_color", CaptionColor);
        root.AddChild(title);

        var headersHost = new VBoxContainer { Name = NodeHeadersHost };
        root.AddChild(headersHost);
        AddHeaderLabels(headersHost, model, compactDetail);

        var chartsHost = new VBoxContainer { Name = NodeChartsHost };
        root.AddChild(chartsHost);
        var anyTsTexture = PopulateTimeSeriesCharts(chartsHost, model, compactDetail, pres);

        var volatileHost = new VBoxContainer { Name = NodeVolatileHost };
        root.AddChild(volatileHost);
        PopulateVolatileTail(volatileHost, model, compactDetail, pres, anyTsTexture);
    }

    /// <summary>Re-headers + bars + grids + detail without rebuilding chart textures (huge FPS win in combat).</summary>
    public static bool TryRefreshVolatileOnly(
        Control host,
        MetricsVisualModel model,
        bool compactDetail = false,
        MetricsVisualUiOptions? presentation = null)
    {
        var pres = presentation ?? TelemetryMetricsUiPreferences.PresentationOptions;
        if (host.GetChildCount() == 0)
            return false;
        if (host.GetChild(0) is not VBoxContainer root || root.Name != "MetricsVisualRoot")
            return false;
        var headers = root.FindChild(NodeHeadersHost, false, false) as VBoxContainer;
        var charts = root.FindChild(NodeChartsHost, false, false) as VBoxContainer;
        var vol = root.FindChild(NodeVolatileHost, false, false) as VBoxContainer;
        if (headers is null || charts is null || vol is null)
            return false;

        foreach (var c in headers.GetChildren())
            c.Free();
        AddHeaderLabels(headers, model, compactDetail);

        var anyTsTexture = ChartsHostHasRenderableTimeSeries(charts, model);
        foreach (var c in vol.GetChildren())
            c.Free();
        PopulateVolatileTail(vol, model, compactDetail, pres, anyTsTexture);
        return true;
    }

    private static void AddHeaderLabels(VBoxContainer headersHost, MetricsVisualModel model, bool compactDetail)
    {
        var headers = model.Headers;
        if (TryUpdateHeaderLabelsInPlace(headersHost, headers, compactDetail))
            return;

        foreach (var c in headersHost.GetChildren())
            c.Free();

        foreach (var h in headers)
        {
            var line = new Label
            {
                Text = h,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            line.AddThemeFontSizeOverride("font_size", compactDetail ? 11 : 12);
            line.AddThemeColorOverride("font_color", MutedColor);
            headersHost.AddChild(line);
        }
    }

    /// <summary>Avoids freeing/recreating header labels when only their text changes (common during combat).</summary>
    private static bool TryUpdateHeaderLabelsInPlace(
        VBoxContainer headersHost,
        IReadOnlyList<string> headers,
        bool compactDetail)
    {
        if (headersHost.GetChildCount() != headers.Count)
            return false;
        if (headers.Count == 0)
            return true;

        var fontPx = compactDetail ? 11 : 12;
        foreach (var c in headersHost.GetChildren())
        {
            if (c is not Label lab)
                return false;
            if (lab.GetThemeFontSize("font_size") != fontPx)
                return false;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            var lab = (Label)headersHost.GetChild(i);
            var t = headers[i];
            if (lab.Text != t)
                lab.Text = t;
        }

        return true;
    }

    /// <summary>True if charts host already has ≥1 rendered texture (matches full rebuild semantics).</summary>
    private static bool ChartsHostHasRenderableTimeSeries(VBoxContainer chartsHost, MetricsVisualModel model)
    {
        if (chartsHost.GetChildCount() == 0)
            return ModelLikelyHasTimeSeriesPlot(model);
        return true;
    }

    private static bool ModelLikelyHasTimeSeriesPlot(MetricsVisualModel model) =>
        model.TimeSeriesCharts.Any(c => c.Series.Any(s => s.Values.Count >= 1));

    /// <summary>Builds timeseries textures into <paramref name="chartsHost"/>; returns whether any chart drew.</summary>
    private static bool PopulateTimeSeriesCharts(
        VBoxContainer chartsHost,
        MetricsVisualModel model,
        bool compactDetail,
        MetricsVisualUiOptions pres)
    {
        var chartW = pres.CompactCharts ? 232 : (compactDetail ? 280 : 320);
        var chartH = pres.CompactCharts ? 56 : (compactDetail ? 84 : 100);
        var anyTsTexture = false;
        if (model.TimeSeriesCharts.Count == 0)
            return false;
        foreach (var chartModel in model.TimeSeriesCharts)
        {
            var tex = MetricsTimeSeriesRenderer.TryBuildChart(
                chartModel.Series,
                width: chartW,
                height: chartH,
                sleekVisuals: pres.SleekCharts);
            if (tex is null)
                continue;
            if (!anyTsTexture)
            {
                AddSectionCaption(chartsHost, "Timeseries", compactDetail || pres.CompactCharts);
                anyTsTexture = true;
            }

            var sub = new Label
            {
                Text = chartModel.SectionTitle,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            sub.AddThemeFontSizeOverride("font_size", compactDetail || pres.CompactCharts ? 11 : 12);
            sub.AddThemeColorOverride("font_color", MutedColor);
            chartsHost.AddChild(sub);

            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", ChartFrameStyleBox(pres.SleekCharts));
            var inner = new Control
            {
                CustomMinimumSize = new Vector2(chartW, chartH),
            };
            var chart = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            chart.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            chart.OffsetRight = chart.OffsetLeft = chart.OffsetTop = chart.OffsetBottom = 0;
            inner.AddChild(chart);
            if (pres.InteractiveHover)
            {
                var catcher = new ChartHoverCatcher(
                    chartModel.Series,
                    chartW,
                    chartH,
                    MetricsTimeSeriesRenderer.PlotMarginLeft,
                    MetricsTimeSeriesRenderer.PlotMarginRight,
                    chartModel.HoverFootnote);
                catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                catcher.OffsetRight = catcher.OffsetLeft = catcher.OffsetTop = catcher.OffsetBottom = 0;
                inner.AddChild(catcher);
            }

            card.AddChild(inner);
            chartsHost.AddChild(card);
        }

        if (anyTsTexture)
        {
            var leg = new Label
            {
                Text = pres.CompactCharts
                    ? "Hover chart for values. Live = Δ / sample; replay = per 5 min."
                    : "Each line scaled to its own max. Live: Δ since previous sample (~1.5s or burst). Replay: totals per 5‑minute wall-clock bucket.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            leg.AddThemeFontSizeOverride("font_size", compactDetail || pres.CompactCharts ? 10 : 11);
            leg.AddThemeColorOverride("font_color", MutedColor);
            chartsHost.AddChild(leg);
        }

        return anyTsTexture;
    }

    private static void PopulateVolatileTail(
        VBoxContainer root,
        MetricsVisualModel model,
        bool compactDetail,
        MetricsVisualUiOptions pres,
        bool anyTsTexture)
    {
        var showRecordingBars = model.RecordingBars.Count > 0
            && (!model.PreferTimeSeriesOverBars || !anyTsTexture);
        if (showRecordingBars)
        {
            AddSectionCaption(root, "Session recording (bars)", compactDetail);
            var maxR = BarListMax(model.RecordingBars);
            foreach (var b in model.RecordingBars)
                AddAnchoredBarRow(root, b.Label, b.Value, maxR, b.Fill, compactDetail);
        }

        var showCardBars = model.CardFlowBars.Count > 0
            && (!model.PreferTimeSeriesOverBars || !anyTsTexture);
        if (showCardBars)
        {
            AddSectionCaption(root, "Card flow (bars)", compactDetail);
            var maxC = BarListMax(model.CardFlowBars);
            foreach (var b in model.CardFlowBars)
                AddAnchoredBarRow(root, b.Label, b.Value, maxC, b.Fill, compactDetail);
        }

        if (model.CardDamageLeaders.Count > 0)
        {
            AddSectionCaption(root, "Damage by card (play-attributed)", compactDetail);
            var hint = new Label
            {
                Text =
                    "Damage from DamageReceived lines while a card play was open (after play started, before play finished). Rows with no attributed damage are omitted — not a full deck review.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            hint.AddThemeFontSizeOverride("font_size", compactDetail ? 10 : 11);
            hint.AddThemeColorOverride("font_color", MutedColor);
            root.AddChild(hint);
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 3);
            foreach (var c in model.CardDamageLeaders)
            {
                var lk = new Label { Text = c.Label };
                lk.AddThemeFontSizeOverride("font_size", compactDetail ? 11 : 12);
                lk.AddThemeColorOverride("font_color", MutedColor);
                var vv = new Label
                {
                    Text = c.Value,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                vv.AddThemeFontSizeOverride("font_size", compactDetail ? 12 : 13);
                vv.AddThemeColorOverride("font_color", ValueColor);
                grid.AddChild(lk);
                grid.AddChild(vv);
            }

            root.AddChild(grid);
        }

        if (model.StatusEffectLeaders.Count > 0)
        {
            AddSectionCaption(root, "Powers & debuffs (combat history)", compactDetail);
            var hint = new Label
            {
                Text =
                    "Counts from combat_history_power_received and combat_history_card_afflicted. “→ player” = inferred debuffs on you / your cards; “→ enemies” = on foes. Same heuristics as NDJSON statusEffect on each line.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            hint.AddThemeFontSizeOverride("font_size", compactDetail ? 10 : 11);
            hint.AddThemeColorOverride("font_color", MutedColor);
            root.AddChild(hint);
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 3);
            foreach (var c in model.StatusEffectLeaders)
            {
                var lk = new Label { Text = c.Label };
                lk.AddThemeFontSizeOverride("font_size", compactDetail ? 11 : 12);
                lk.AddThemeColorOverride("font_color", MutedColor);
                var vv = new Label
                {
                    Text = c.Value,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                vv.AddThemeFontSizeOverride("font_size", compactDetail ? 12 : 13);
                vv.AddThemeColorOverride("font_color", ValueColor);
                grid.AddChild(lk);
                grid.AddChild(vv);
            }

            root.AddChild(grid);
        }

        if (model.RoomVisitBars.Count > 0)
        {
            AddSectionCaption(root, "Visited map rooms (run save)", compactDetail);
            var hint = new Label
            {
                Text = "Counts by map_point_type / room_type from map_point_history (current_run.save). Shown on Overview & Run.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            hint.AddThemeFontSizeOverride("font_size", compactDetail ? 10 : 11);
            hint.AddThemeColorOverride("font_color", MutedColor);
            root.AddChild(hint);
            var maxM = BarListMax(model.RoomVisitBars);
            foreach (var b in model.RoomVisitBars)
                AddAnchoredBarRow(root, b.Label, b.Value, maxM, b.Fill, compactDetail);
        }

        if (model.HandBars.Count > 0)
        {
            AddSectionCaption(root, "Hands (energy steps)", compactDetail);
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            row.AddThemeConstantOverride("separation", 4);
            var maxSteps = model.HandBars.Max(h => h.Steps);
            if (maxSteps < 1)
                maxSteps = 1;
            const float colH = 80f;
            foreach (var h in model.HandBars)
            {
                var col = new VBoxContainer { CustomMinimumSize = new Vector2(12, colH) };
                var frac = h.Steps / (double)maxSteps;
                var barPx = Math.Max(h.Steps > 0 ? 4 : 0, (int)(colH * frac));
                var spacer = new Control
                {
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(10, 1),
                };
                col.AddChild(spacer);
                var rect = new ColorRect
                {
                    Color = new Color(0.35f, 0.82f, 0.95f, 1f),
                    CustomMinimumSize = new Vector2(10, barPx),
                };
                col.AddChild(rect);
                row.AddChild(col);
            }

            root.AddChild(row);
            var legend = new Label
            {
                Text = "Oldest ← newest",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            legend.AddThemeFontSizeOverride("font_size", 10);
            legend.AddThemeColorOverride("font_color", MutedColor);
            root.AddChild(legend);
        }

        if (model.Counters.Count > 0)
        {
            AddSectionCaption(root, "Counters", compactDetail);
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 3);
            foreach (var c in model.Counters)
            {
                var lk = new Label { Text = c.Label };
                lk.AddThemeFontSizeOverride("font_size", compactDetail ? 11 : 12);
                lk.AddThemeColorOverride("font_color", MutedColor);
                var vv = new Label
                {
                    Text = c.Value,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                vv.AddThemeFontSizeOverride("font_size", compactDetail ? 12 : 13);
                vv.AddThemeColorOverride("font_color", ValueColor);
                grid.AddChild(lk);
                grid.AddChild(vv);
            }

            root.AddChild(grid);
        }

        if (!compactDetail)
        {
            AddSectionCaption(root, "Full detail (text)", false);
            var detail = new RichTextLabel
            {
                Text = string.IsNullOrEmpty(model.DetailText) ? "—" : model.DetailText,
                BbcodeEnabled = false,
                FitContent = false,
                CustomMinimumSize = new Vector2(0, 120),
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                ScrollActive = true,
                ScrollFollowing = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            detail.AddThemeFontSizeOverride("normal_font_size", 11);
            root.AddChild(detail);
        }
    }

    private static double BarListMax(IReadOnlyList<MetricBar> bars)
    {
        var m = bars.Max(b => b.Value);
        return m < 1d ? 1d : m;
    }

    private static StyleBoxFlat ChartFrameStyleBox(bool sleek)
    {
        var s = new StyleBoxFlat();
        if (sleek)
        {
            s.BgColor = new Color(0.045f, 0.048f, 0.055f, 0.72f);
            s.BorderColor = new Color(0.22f, 0.26f, 0.32f, 0.85f);
            s.SetBorderWidthAll(1);
            s.SetCornerRadiusAll(6);
            s.ContentMarginLeft = s.ContentMarginRight = 5;
            s.ContentMarginTop = s.ContentMarginBottom = 5;
        }
        else
        {
            s.BgColor = Colors.Transparent;
            s.SetBorderWidthAll(0);
            s.ContentMarginLeft = s.ContentMarginRight = s.ContentMarginTop = s.ContentMarginBottom = 0;
        }

        return s;
    }

    private static void AddSectionCaption(VBoxContainer root, string text, bool compact)
    {
        var cap = new Label { Text = text };
        cap.AddThemeFontSizeOverride("font_size", compact ? 12 : 13);
        cap.AddThemeColorOverride("font_color", CaptionColor);
        root.AddChild(cap);
    }

    /// <summary>Horizontal bar using <see cref="ColorRect"/> (reliable vs game-themed <see cref="ProgressBar"/>).</summary>
    private static void AddAnchoredBarRow(VBoxContainer root, string label, double value, double max, Color fill, bool compact)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var lab = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(compact ? 72 : 88, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lab.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
        lab.AddThemeColorOverride("font_color", MutedColor);

        var track = new Control
        {
            CustomMinimumSize = new Vector2(64, compact ? 18 : 22),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
        };
        var bg = new ColorRect { Color = BarTrackColor };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.OffsetRight = 0;
        bg.OffsetBottom = 0;
        bg.OffsetLeft = 0;
        bg.OffsetTop = 0;
        track.AddChild(bg);

        var denom = max > 0 ? max : 1d;
        var frac = Math.Clamp(value / denom, 0d, 1d);
        var fillRect = new ColorRect { Color = fill };
        fillRect.AnchorLeft = 0;
        fillRect.AnchorTop = 0;
        fillRect.AnchorBottom = 1;
        fillRect.AnchorRight = (float)frac;
        fillRect.OffsetLeft = 2;
        fillRect.OffsetTop = 2;
        fillRect.OffsetRight = -2;
        fillRect.OffsetBottom = -2;
        track.AddChild(fillRect);

        var val = new Label
        {
            Text = ((long)value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            CustomMinimumSize = new Vector2(44, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        val.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
        val.AddThemeColorOverride("font_color", ValueColor);

        row.AddChild(lab);
        row.AddChild(track);
        row.AddChild(val);
        root.AddChild(row);
    }
}
