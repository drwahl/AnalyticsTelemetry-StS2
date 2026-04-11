using System.IO;
using AnalyticsTelemetry.AnalyticsTelemetryCode;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Top-right <b>Analytics</b> button + optional log panel (children of <see cref="CanvasLayer"/> directly,
/// no full-screen hit target). Built only from engine node types
/// (<see cref="Node"/>, <see cref="CanvasLayer"/>, etc.) so the mod does not rely on a custom
/// <c>partial</c> GodotObject subclass (that path threw <c>Undefined resource string ID:0x80070057</c>
/// on <see cref="Node.AddChild"/> in play).
/// </summary>
public static class TelemetryDebugOverlayUi
{
    private static bool _viewportSizeHooked;
    private static CanvasLayer? _layer;
    private static CanvasLayer? _flyoutLayer;
    private static Control? _flyoutHost;
    private static PanelContainer? _scopeFlyoutPanel;
    private static PanelContainer? _datasetFlyoutPanel;
    private static VBoxContainer? _datasetFlyoutVBox;
    private static PanelContainer? _actFlyoutPanel;
    private static VBoxContainer? _actFlyoutVBox;
    private static PanelContainer? _combatFlyoutPanel;
    private static VBoxContainer? _combatFlyoutVBox;
    private static Button? _scopeButton;
    private static Button? _datasetButton;
    private static Button? _actTargetButton;
    private static Button? _combatTargetButton;
    private static int _scopeIndex;
    private static PanelContainer? _panel;
    private static ScrollContainer? _metricsScroll;
    private static Control? _metricsVisualHost;
    private static RichTextLabel? _logLabel;
    private static Label? _logCaption;
    private static string _lastLogText = "";
    private static string _lastChartTextureSignature = "\0";
    private static string _lastVolatileUiSignature = "\0";
    /// <summary>Caps metrics UI rebuild rate — full chart textures were hot when combined with volatile counters.</summary>
    private static ulong _metricsRefreshEarliestTicksMsec;
    private const ulong MetricsPaneMinRefreshIntervalMsec = 450;
    private static CheckButton? _showFullDetailCheck;
    private static CheckButton? _showRecentEventsCheck;
    private static CheckButton? _rawNdjsonCheck;
    private static CheckButton? _compactMetricsPanelCheck;
    private static CheckButton? _sleekChartsCheck;
    private static CheckButton? _chartHoverCheck;
    private static CheckButton? _showLiveThroughputChartCheck;
    private static CheckButton? _dmgChartInCheck;
    private static CheckButton? _dmgChartOutCheck;
    private static CheckButton? _dmgChartUnkCheck;
    private static CheckButton? _dmgChartBlockCheck;

    /// <summary>Root node name used for idempotent attach (<see cref="MainFile"/>).</summary>
    public const string RootNodeName = "TelemetryDebugOverlayRoot";

    /// <summary>Draw above typical game UI; keep below screen-space popups if those use even higher layers.</summary>
    private const int CanvasLayerOrder = 1_000_000;
    private const int ScopeActIndex = 2;
    private const int ScopeCombatIndex = 3;

    private static readonly string[] ScopeLabels =
    {
        "Overview",
        "Run (session file)",
        "Act",
        "Combat",
        "Hands",
        "Multiplayer",
    };

    public static Node CreateRoot()
    {
        TelemetryMetricsUiPreferences.LoadFromDisk();

        var root = new Node { Name = RootNodeName };

        _layer = new CanvasLayer
        {
            Layer = CanvasLayerOrder,
            ProcessMode = Node.ProcessModeEnum.Always,
            Visible = true,
        };
        root.AddChild(_layer);

        // OptionButton popups are separate Windows and still composite under our semi-transparent panel.
        // Scope list uses a higher canvas layer while open. Keep that layer hidden when idle: some builds
        // still composite an empty CanvasLayer as a transparent pass on top, which dims the main overlay.
        _flyoutLayer = new CanvasLayer
        {
            Layer = CanvasLayerOrder + 1,
            ProcessMode = Node.ProcessModeEnum.Always,
            Visible = false,
        };
        root.AddChild(_flyoutLayer);

        _flyoutHost = new Control
        {
            Name = "TelemetryScopeFlyoutHost",
            Visible = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _flyoutHost.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _flyoutHost.OffsetRight = 0;
        _flyoutHost.OffsetBottom = 0;
        _flyoutHost.OffsetLeft = 0;
        _flyoutHost.OffsetTop = 0;
        _flyoutLayer.AddChild(_flyoutHost);

        var flyoutBlocker = new Control
        {
            Name = "TelemetryScopeFlyoutBlocker",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 0,
        };
        flyoutBlocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        flyoutBlocker.OffsetRight = 0;
        flyoutBlocker.OffsetBottom = 0;
        flyoutBlocker.OffsetLeft = 0;
        flyoutBlocker.OffsetTop = 0;
        flyoutBlocker.GuiInput += OnScopeFlyoutBlockerGuiInput;
        _flyoutHost.AddChild(flyoutBlocker);

        _scopeFlyoutPanel = new PanelContainer
        {
            Name = "TelemetryScopeFlyoutPanel",
            Visible = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 10,
        };
        var flyoutFrame = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.11f, 0.13f, 1f),
            BorderColor = new Color(0.85f, 0.72f, 0.4f, 1f),
        };
        flyoutFrame.SetCornerRadiusAll(8);
        flyoutFrame.SetBorderWidthAll(2);
        flyoutFrame.SetContentMarginAll(6);
        _scopeFlyoutPanel.AddThemeStyleboxOverride("panel", flyoutFrame);

        var flyoutVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (var i = 0; i < ScopeLabels.Length; i++)
        {
            var idx = i;
            var row = new Button
            {
                Text = ScopeLabels[i],
                Flat = false,
                FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(260, 32),
            };
            row.AddThemeFontSizeOverride("font_size", 13);
            row.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
            row.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.95f, 1f));
            row.AddThemeColorOverride("font_pressed_color", new Color(0.85f, 0.88f, 0.8f, 1f));
            static StyleBoxFlat RowBox(Color bg)
            {
                var s = new StyleBoxFlat { BgColor = bg };
                s.SetCornerRadiusAll(4);
                s.ContentMarginLeft = 10;
                s.ContentMarginRight = 10;
                s.ContentMarginTop = 4;
                s.ContentMarginBottom = 4;
                return s;
            }

            row.AddThemeStyleboxOverride("normal", RowBox(new Color(0.14f, 0.15f, 0.17f, 1f)));
            row.AddThemeStyleboxOverride("hover", RowBox(new Color(0.22f, 0.24f, 0.28f, 1f)));
            row.AddThemeStyleboxOverride("pressed", RowBox(new Color(0.18f, 0.2f, 0.22f, 1f)));
            row.Pressed += () => OnScopeFlyoutRowPressed(idx);
            flyoutVBox.AddChild(row);
        }

        _scopeFlyoutPanel.AddChild(flyoutVBox);
        _flyoutHost.AddChild(_scopeFlyoutPanel);

        _datasetFlyoutPanel = new PanelContainer
        {
            Name = "TelemetryDatasetFlyoutPanel",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 10,
        };
        _datasetFlyoutPanel.AddThemeStyleboxOverride("panel", flyoutFrame);

        var datasetScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(300, 280),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _datasetFlyoutVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        datasetScroll.AddChild(_datasetFlyoutVBox);
        _datasetFlyoutPanel.AddChild(datasetScroll);
        _flyoutHost.AddChild(_datasetFlyoutPanel);

        _actFlyoutPanel = new PanelContainer
        {
            Name = "TelemetryActFlyoutPanel",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 10,
        };
        _actFlyoutPanel.AddThemeStyleboxOverride("panel", flyoutFrame);
        var actScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(300, 220),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _actFlyoutVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actScroll.AddChild(_actFlyoutVBox);
        _actFlyoutPanel.AddChild(actScroll);
        _flyoutHost.AddChild(_actFlyoutPanel);

        _combatFlyoutPanel = new PanelContainer
        {
            Name = "TelemetryCombatFlyoutPanel",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 10,
        };
        _combatFlyoutPanel.AddThemeStyleboxOverride("panel", flyoutFrame);
        var combatScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(300, 220),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _combatFlyoutVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        combatScroll.AddChild(_combatFlyoutVBox);
        _combatFlyoutPanel.AddChild(combatScroll);
        _flyoutHost.AddChild(_combatFlyoutPanel);

        // Do not use a full-viewport parent Control (even with MouseFilter.Ignore): some Godot builds still
        // block world/UI clicks behind the overlay. Parent panel + toggle directly to CanvasLayer so only
        // those rects hit-test.

        _panel = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 10,
            ZAsRelative = false,
        };
        // Fully opaque: semi-transparent panel + mixed child z-order made metrics/log look like a dark veil.
        var panelFrame = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.045f, 0.052f, 1f),
            BorderColor = new Color(0.72f, 0.62f, 0.35f, 0.9f),
        };
        panelFrame.SetCornerRadiusAll(10);
        panelFrame.SetBorderWidthAll(2);
        panelFrame.SetContentMarginAll(12);
        _panel.AddThemeStyleboxOverride("panel", panelFrame);

        // Top-right column: avoids overlapping the in-game combat log (usually top-left).
        _panel.SetAnchor(Side.Left, 1f, false);
        _panel.SetAnchor(Side.Right, 1f, false);
        _panel.SetAnchor(Side.Top, 0f, false);
        _panel.SetAnchor(Side.Bottom, 1f, false);
        _panel.OffsetLeft = -648;
        _panel.OffsetRight = -8;
        _panel.OffsetTop = 46;
        _panel.OffsetBottom = -12;
        _panel.CustomMinimumSize = new Vector2(320, 200);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        var hint = new Label
        {
            Text = "Data: NDJSON selection (live vs replay). Scope: chart dimension. Act / Combat target pickers pin a bucket (enabled only for that scope). Popups share one layer.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.77f, 0.7f));

        _scopeIndex = 0;
        _scopeButton = new Button
        {
            Text = FormatScopeButtonCaption(0),
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _scopeButton.AddThemeFontSizeOverride("font_size", 13);
        _scopeButton.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        _scopeButton.Pressed += ToggleScopeFlyout;

        _datasetButton = new Button
        {
            Text = FormatDatasetButtonCaption(),
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _datasetButton.AddThemeFontSizeOverride("font_size", 13);
        _datasetButton.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        _datasetButton.Pressed += ToggleDatasetFlyout;

        _actTargetButton = new Button
        {
            Text = FormatActTargetButtonCaption(),
            Disabled = true,
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _actTargetButton.AddThemeFontSizeOverride("font_size", 13);
        _actTargetButton.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        _actTargetButton.Pressed += ToggleActFlyout;

        _combatTargetButton = new Button
        {
            Text = FormatCombatTargetButtonCaption(),
            Disabled = true,
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _combatTargetButton.AddThemeFontSizeOverride("font_size", 13);
        _combatTargetButton.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        _combatTargetButton.Pressed += ToggleCombatFlyout;

        var clearHistoricBtn = new Button
        {
            Text = "Clear chart & counter memory",
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText =
                "Resets live charts, counters, hand buffer, replay cache, staged energy steps, and the rolling event preview. "
                + "Does not delete NDJSON on disk.",
        };
        clearHistoricBtn.AddThemeFontSizeOverride("font_size", 13);
        clearHistoricBtn.Pressed += OnClearHistoricMetricsPressed;

        _showFullDetailCheck = new CheckButton { Text = "Show full text block (duplicate of counters)" };
        _showFullDetailCheck.ButtonPressed = false;
        _showFullDetailCheck.AddThemeFontSizeOverride("font_size", 12);
        _showFullDetailCheck.Toggled += OnOverlayDisplayToggled;

        _showRecentEventsCheck = new CheckButton { Text = "Show recent events (rolling log)" };
        _showRecentEventsCheck.ButtonPressed = true;
        _showRecentEventsCheck.AddThemeFontSizeOverride("font_size", 12);
        _showRecentEventsCheck.Toggled += OnRecentEventsToggled;

        _rawNdjsonCheck = new CheckButton { Text = "Raw NDJSON lines (debug)" };
        _rawNdjsonCheck.ButtonPressed = false;
        _rawNdjsonCheck.AddThemeFontSizeOverride("font_size", 12);
        _rawNdjsonCheck.Toggled += OnOverlayDisplayToggled;

        _compactMetricsPanelCheck = new CheckButton
        {
            Text = "Compact panel (narrow strip, shorter — good for small charts only)",
        };
        _compactMetricsPanelCheck.ButtonPressed = TelemetryMetricsUiPreferences.CompactPanel;
        _compactMetricsPanelCheck.AddThemeFontSizeOverride("font_size", 12);
        _compactMetricsPanelCheck.Toggled += OnCompactMetricsPanelToggled;

        _sleekChartsCheck = new CheckButton { Text = "Sleek chart style (darker plot, subtler grid)" };
        _sleekChartsCheck.ButtonPressed = TelemetryMetricsUiPreferences.SleekCharts;
        _sleekChartsCheck.AddThemeFontSizeOverride("font_size", 12);
        _sleekChartsCheck.Toggled += OnSleekChartsToggled;

        _chartHoverCheck = new CheckButton { Text = "Chart hover values (crosshair + tooltip)" };
        _chartHoverCheck.ButtonPressed = TelemetryMetricsUiPreferences.ChartHover;
        _chartHoverCheck.AddThemeFontSizeOverride("font_size", 12);
        _chartHoverCheck.Toggled += OnChartHoverToggled;

        _showLiveThroughputChartCheck = new CheckButton
        {
            Text = "Show live throughput chart (debug — NDJSON/history Δ per sample)",
        };
        _showLiveThroughputChartCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowLiveThroughputChart;
        _showLiveThroughputChartCheck.AddThemeFontSizeOverride("font_size", 12);
        _showLiveThroughputChartCheck.Toggled += OnShowLiveThroughputChartToggled;

        var dmgCap = new Label { Text = "Dmg charts — show lines:" };
        dmgCap.AddThemeFontSizeOverride("font_size", 12);
        dmgCap.AddThemeColorOverride("font_color", new Color(0.82f, 0.84f, 0.78f, 1f));
        _dmgChartInCheck = new CheckButton { Text = "Dmg in (to player)" };
        _dmgChartInCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageIn;
        _dmgChartInCheck.AddThemeFontSizeOverride("font_size", 12);
        _dmgChartInCheck.Toggled += OnDamageChartSeriesToggled;
        _dmgChartOutCheck = new CheckButton { Text = "Dmg out (to enemies)" };
        _dmgChartOutCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageOut;
        _dmgChartOutCheck.AddThemeFontSizeOverride("font_size", 12);
        _dmgChartOutCheck.Toggled += OnDamageChartSeriesToggled;
        _dmgChartUnkCheck = new CheckButton { Text = "Dmg unclassified" };
        _dmgChartUnkCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageUnk;
        _dmgChartUnkCheck.AddThemeFontSizeOverride("font_size", 12);
        _dmgChartUnkCheck.Toggled += OnDamageChartSeriesToggled;
        _dmgChartBlockCheck = new CheckButton { Text = "Block" };
        _dmgChartBlockCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartBlock;
        _dmgChartBlockCheck.AddThemeFontSizeOverride("font_size", 12);
        _dmgChartBlockCheck.Toggled += OnDamageChartSeriesToggled;

        var metricsCaption = new Label { Text = "— Metrics —" };
        metricsCaption.AddThemeFontSizeOverride("font_size", 13);
        metricsCaption.AddThemeColorOverride("font_color", new Color(0.88f, 0.8f, 0.5f));
        _metricsScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 260),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _metricsVisualHost = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(280, 180),
        };
        _metricsScroll.AddChild(_metricsVisualHost);

        _logCaption = new Label { Text = "— Recent events —" };
        _logCaption.AddThemeFontSizeOverride("font_size", 13);
        _logCaption.AddThemeColorOverride("font_color", new Color(0.88f, 0.8f, 0.5f));
        _logLabel = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 140),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitContent = false,
            ScrollActive = true,
            ScrollFollowing = false,
            BbcodeEnabled = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _logLabel.AddThemeFontSizeOverride("normal_font_size", 12);
        _logLabel.AddThemeColorOverride("default_color", new Color(0.9f, 0.91f, 0.86f, 1f));

        v.AddChild(hint);
        v.AddChild(_scopeButton);
        v.AddChild(_datasetButton);
        v.AddChild(_actTargetButton);
        v.AddChild(_combatTargetButton);
        v.AddChild(clearHistoricBtn);
        v.AddChild(_showFullDetailCheck);
        v.AddChild(_showRecentEventsCheck);
        v.AddChild(_rawNdjsonCheck);
        v.AddChild(_compactMetricsPanelCheck);
        v.AddChild(_sleekChartsCheck);
        v.AddChild(_chartHoverCheck);
        v.AddChild(_showLiveThroughputChartCheck);
        v.AddChild(dmgCap);
        v.AddChild(_dmgChartInCheck);
        v.AddChild(_dmgChartOutCheck);
        v.AddChild(_dmgChartUnkCheck);
        v.AddChild(_dmgChartBlockCheck);
        v.AddChild(metricsCaption);
        v.AddChild(_metricsScroll);
        v.AddChild(_logCaption);
        v.AddChild(_logLabel);
        _panel.AddChild(v);
        _layer.AddChild(_panel);
        Callable.From(ApplyMetricsPanelLayout).CallDeferred();

        var toggle = new Button
        {
            Text = "Analytics",
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 11,
            ZAsRelative = false,
        };
        toggle.CustomMinimumSize = new Vector2(100, 38);
        toggle.AddThemeFontSizeOverride("font_size", 16);
        toggle.AddThemeColorOverride("font_color", new Color(0.05f, 0.08f, 0.05f, 1f));
        toggle.AddThemeColorOverride("font_hover_color", new Color(0.05f, 0.08f, 0.05f, 1f));
        toggle.AddThemeColorOverride("font_pressed_color", new Color(0.05f, 0.08f, 0.05f, 1f));
        static StyleBoxFlat Box(Color bg)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(6);
            s.ContentMarginLeft = s.ContentMarginRight = 12;
            s.ContentMarginTop = s.ContentMarginBottom = 6;
            return s;
        }

        toggle.AddThemeStyleboxOverride("normal", Box(new Color(0.35f, 0.92f, 0.45f, 0.96f)));
        toggle.AddThemeStyleboxOverride("hover", Box(new Color(0.45f, 1f, 0.55f, 0.98f)));
        toggle.AddThemeStyleboxOverride("pressed", Box(new Color(0.22f, 0.72f, 0.32f, 0.96f)));
        toggle.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        toggle.OffsetLeft = -118;
        toggle.OffsetRight = -10;
        toggle.OffsetTop = 8;
        toggle.OffsetBottom = 46;
        toggle.Pressed += TogglePanel;
        _layer.AddChild(toggle);

        return root;
    }

    private static string FormatScopeButtonCaption(int index) =>
        $"Scope: {ScopeLabels[Mathf.Clamp(index, 0, ScopeLabels.Length - 1)]}  ▼";

    private static string FormatDatasetButtonCaption()
    {
        return TelemetryDatasetUiState.Selection.Kind switch
        {
            TelemetryDatasetKind.CurrentSession => "Data: Current session (live)  ▼",
            TelemetryDatasetKind.AllSavedSessions => "Data: All saved sessions  ▼",
            TelemetryDatasetKind.Last24Hours => "Data: Last 24 hours  ▼",
            TelemetryDatasetKind.Last7Days => "Data: Last 7 days  ▼",
            TelemetryDatasetKind.Last30Days => "Data: Last 30 days  ▼",
            TelemetryDatasetKind.Last365Days => "Data: Last 365 days  ▼",
            TelemetryDatasetKind.SingleSessionFile =>
                $"Data: {Path.GetFileName(TelemetryDatasetUiState.Selection.SingleFileFullPath ?? "?")}  ▼",
            _ => "Data: …  ▼",
        };
    }

    private static string FormatActTargetButtonCaption()
    {
        if (_scopeIndex != ScopeActIndex)
            return "Act target: (Scope: Act)  ▼";
        return string.IsNullOrEmpty(TelemetryActUiState.SelectedActKey)
            ? "Act: Follow map act  ▼"
            : $"Act: {TelemetryActUiState.SelectedActKey}  ▼";
    }

    private static void SyncActTargetButtonState()
    {
        if (_actTargetButton is null)
            return;
        _actTargetButton.Disabled = _scopeIndex != ScopeActIndex;
        _actTargetButton.Text = FormatActTargetButtonCaption();
    }

    private static string FormatCombatTargetButtonCaption()
    {
        if (_scopeIndex != ScopeCombatIndex)
            return "Combat target: (Scope: Combat)  ▼";
        return TelemetryCombatUiState.SelectedCombatOrdinal is not { } co
            ? "Combat: Follow live fight  ▼"
            : $"Combat: Pinned #{co}  ▼";
    }

    private static void SyncCombatTargetButtonState()
    {
        if (_combatTargetButton is null)
            return;
        _combatTargetButton.Disabled = _scopeIndex != ScopeCombatIndex;
        _combatTargetButton.Text = FormatCombatTargetButtonCaption();
    }

    private static void SyncDrillTargetButtons()
    {
        SyncActTargetButtonState();
        SyncCombatTargetButtonState();
    }

    private static void RebuildActFlyoutMenu()
    {
        if (_actFlyoutVBox is null)
            return;
        while (_actFlyoutVBox.GetChildCount() > 0)
        {
            var c = _actFlyoutVBox.GetChild(0);
            _actFlyoutVBox.RemoveChild(c);
            c.Free();
        }

        void Add(string label, string? actKey)
        {
            var row = CreateActFlyoutRow(label, actKey);
            _actFlyoutVBox!.AddChild(row);
        }

        Add("Follow map act (current act in run)", null);
        foreach (var k in TelemetryMetricsStore.ListActKeysForUi(TelemetryDatasetUiState.Selection))
            Add(k, k);
    }

    private static Button CreateActFlyoutRow(string label, string? actKey)
    {
        var row = new Button
        {
            Text = label,
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(260, 0),
        };
        row.AddThemeFontSizeOverride("font_size", 12);
        row.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        row.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.95f, 1f));
        row.AddThemeColorOverride("font_pressed_color", new Color(0.85f, 0.88f, 0.8f, 1f));
        static StyleBoxFlat RowBox(Color bg)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(4);
            s.ContentMarginLeft = 10;
            s.ContentMarginRight = 10;
            s.ContentMarginTop = 4;
            s.ContentMarginBottom = 4;
            return s;
        }

        row.AddThemeStyleboxOverride("normal", RowBox(new Color(0.14f, 0.15f, 0.17f, 1f)));
        row.AddThemeStyleboxOverride("hover", RowBox(new Color(0.22f, 0.24f, 0.28f, 1f)));
        row.AddThemeStyleboxOverride("pressed", RowBox(new Color(0.18f, 0.2f, 0.22f, 1f)));
        var captured = actKey;
        row.Pressed += () => OnActFlyoutRowPressed(captured);
        return row;
    }

    private static void OnActFlyoutRowPressed(string? actKey)
    {
        CloseScopeFlyout();
        TelemetryActUiState.SelectedActKey = actKey;
        SyncDrillTargetButtons();
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void RebuildCombatFlyoutMenu()
    {
        if (_combatFlyoutVBox is null)
            return;
        while (_combatFlyoutVBox.GetChildCount() > 0)
        {
            var c = _combatFlyoutVBox.GetChild(0);
            _combatFlyoutVBox.RemoveChild(c);
            c.Free();
        }

        void Add(string label, int? combatOrdinal)
        {
            var row = CreateCombatFlyoutRow(label, combatOrdinal);
            _combatFlyoutVBox!.AddChild(row);
        }

        Add("Follow live combat (current fight #)", null);
        foreach (var n in TelemetryMetricsStore.ListCombatOrdinalsForUi(TelemetryDatasetUiState.Selection))
            Add($"Combat #{n}", n);
    }

    private static Button CreateCombatFlyoutRow(string label, int? combatOrdinal)
    {
        var row = new Button
        {
            Text = label,
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(260, 0),
        };
        row.AddThemeFontSizeOverride("font_size", 12);
        row.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        row.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.95f, 1f));
        row.AddThemeColorOverride("font_pressed_color", new Color(0.85f, 0.88f, 0.8f, 1f));
        static StyleBoxFlat RowBox(Color bg)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(4);
            s.ContentMarginLeft = 10;
            s.ContentMarginRight = 10;
            s.ContentMarginTop = 4;
            s.ContentMarginBottom = 4;
            return s;
        }

        row.AddThemeStyleboxOverride("normal", RowBox(new Color(0.14f, 0.15f, 0.17f, 1f)));
        row.AddThemeStyleboxOverride("hover", RowBox(new Color(0.22f, 0.24f, 0.28f, 1f)));
        row.AddThemeStyleboxOverride("pressed", RowBox(new Color(0.18f, 0.2f, 0.22f, 1f)));
        var captured = combatOrdinal;
        row.Pressed += () => OnCombatFlyoutRowPressed(captured);
        return row;
    }

    private static void OnCombatFlyoutRowPressed(int? combatOrdinal)
    {
        CloseScopeFlyout();
        TelemetryCombatUiState.SelectedCombatOrdinal = combatOrdinal;
        SyncDrillTargetButtons();
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void ToggleScopeFlyout()
    {
        if (_flyoutLayer is null || _scopeButton is null || !_flyoutLayer.IsInsideTree())
            return;
        if (_flyoutLayer.Visible && _scopeFlyoutPanel?.Visible == true)
            CloseScopeFlyout();
        else
            OpenScopeFlyout();
    }

    private static void ToggleDatasetFlyout()
    {
        if (_flyoutLayer is null || _datasetButton is null || !_flyoutLayer.IsInsideTree())
            return;
        if (_flyoutLayer.Visible && _datasetFlyoutPanel?.Visible == true)
            CloseScopeFlyout();
        else
            OpenDatasetFlyout();
    }

    private static void OpenScopeFlyout()
    {
        if (_flyoutLayer is null || _scopeButton is null || _scopeFlyoutPanel is null || _datasetFlyoutPanel is null)
            return;
        _datasetFlyoutPanel.Visible = false;
        if (_actFlyoutPanel is not null)
            _actFlyoutPanel.Visible = false;
        if (_combatFlyoutPanel is not null)
            _combatFlyoutPanel.Visible = false;
        _scopeFlyoutPanel.Visible = true;
        _flyoutLayer.Visible = true;
        Callable.From(PositionScopeFlyoutPanel).CallDeferred();
    }

    private static void OpenDatasetFlyout()
    {
        if (_flyoutLayer is null || _datasetButton is null || _scopeFlyoutPanel is null || _datasetFlyoutPanel is null)
            return;
        RebuildDatasetFlyoutMenu();
        _scopeFlyoutPanel.Visible = false;
        if (_actFlyoutPanel is not null)
            _actFlyoutPanel.Visible = false;
        if (_combatFlyoutPanel is not null)
            _combatFlyoutPanel.Visible = false;
        _datasetFlyoutPanel.Visible = true;
        _flyoutLayer.Visible = true;
        Callable.From(PositionDatasetFlyoutPanel).CallDeferred();
    }

    private static void ToggleActFlyout()
    {
        if (_flyoutLayer is null || _actTargetButton is null || !_flyoutLayer.IsInsideTree())
            return;
        if (_scopeIndex != ScopeActIndex)
            return;
        if (_flyoutLayer.Visible && _actFlyoutPanel?.Visible == true)
            CloseScopeFlyout();
        else
            OpenActFlyout();
    }

    private static void OpenActFlyout()
    {
        if (_flyoutLayer is null || _actTargetButton is null || _scopeFlyoutPanel is null || _datasetFlyoutPanel is null
            || _actFlyoutPanel is null || _actFlyoutVBox is null)
            return;
        RebuildActFlyoutMenu();
        _scopeFlyoutPanel.Visible = false;
        _datasetFlyoutPanel.Visible = false;
        if (_combatFlyoutPanel is not null)
            _combatFlyoutPanel.Visible = false;
        _actFlyoutPanel.Visible = true;
        _flyoutLayer.Visible = true;
        Callable.From(PositionActFlyoutPanel).CallDeferred();
    }

    private static void ToggleCombatFlyout()
    {
        if (_flyoutLayer is null || _combatTargetButton is null || !_flyoutLayer.IsInsideTree())
            return;
        if (_scopeIndex != ScopeCombatIndex)
            return;
        if (_flyoutLayer.Visible && _combatFlyoutPanel?.Visible == true)
            CloseScopeFlyout();
        else
            OpenCombatFlyout();
    }

    private static void OpenCombatFlyout()
    {
        if (_flyoutLayer is null || _combatTargetButton is null || _scopeFlyoutPanel is null || _datasetFlyoutPanel is null
            || _combatFlyoutPanel is null || _combatFlyoutVBox is null)
            return;
        RebuildCombatFlyoutMenu();
        _scopeFlyoutPanel.Visible = false;
        _datasetFlyoutPanel.Visible = false;
        if (_actFlyoutPanel is not null)
            _actFlyoutPanel.Visible = false;
        _combatFlyoutPanel.Visible = true;
        _flyoutLayer.Visible = true;
        Callable.From(PositionCombatFlyoutPanel).CallDeferred();
    }

    private static void RebuildDatasetFlyoutMenu()
    {
        if (_datasetFlyoutVBox is null)
            return;
        while (_datasetFlyoutVBox.GetChildCount() > 0)
        {
            var c = _datasetFlyoutVBox.GetChild(0);
            _datasetFlyoutVBox.RemoveChild(c);
            c.Free();
        }

        void Add(string label, TelemetryDatasetSelection sel)
        {
            var row = CreateDatasetFlyoutRow(label, sel);
            _datasetFlyoutVBox!.AddChild(row);
        }

        Add("Current session (live)", TelemetryDatasetSelection.Current);
        Add("All saved sessions", new TelemetryDatasetSelection(TelemetryDatasetKind.AllSavedSessions, null));
        Add("Last 24 hours", new TelemetryDatasetSelection(TelemetryDatasetKind.Last24Hours, null));
        Add("Last 7 days", new TelemetryDatasetSelection(TelemetryDatasetKind.Last7Days, null));
        Add("Last 30 days", new TelemetryDatasetSelection(TelemetryDatasetKind.Last30Days, null));
        Add("Last 365 days", new TelemetryDatasetSelection(TelemetryDatasetKind.Last365Days, null));

        var sep = new Label
        {
            Text = "— Past runs (newest first) —",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        sep.AddThemeFontSizeOverride("font_size", 11);
        sep.AddThemeColorOverride("font_color", new Color(0.65f, 0.67f, 0.6f));
        _datasetFlyoutVBox.AddChild(sep);

        foreach (var d in TelemetryDatasetCatalog.ListSessions(32))
        {
            var path = d.FullPath;
            Add(
                $"{d.FileName}  ·  {d.LastWriteUtc:yyyy-MM-dd HH:mm} UTC",
                new TelemetryDatasetSelection(TelemetryDatasetKind.SingleSessionFile, path));
        }
    }

    private static Button CreateDatasetFlyoutRow(string label, TelemetryDatasetSelection sel)
    {
        var row = new Button
        {
            Text = label,
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(260, 0),
        };
        row.AddThemeFontSizeOverride("font_size", 12);
        row.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.88f, 1f));
        row.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.95f, 1f));
        row.AddThemeColorOverride("font_pressed_color", new Color(0.85f, 0.88f, 0.8f, 1f));
        static StyleBoxFlat RowBox(Color bg)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(4);
            s.ContentMarginLeft = 10;
            s.ContentMarginRight = 10;
            s.ContentMarginTop = 4;
            s.ContentMarginBottom = 4;
            return s;
        }

        row.AddThemeStyleboxOverride("normal", RowBox(new Color(0.14f, 0.15f, 0.17f, 1f)));
        row.AddThemeStyleboxOverride("hover", RowBox(new Color(0.22f, 0.24f, 0.28f, 1f)));
        row.AddThemeStyleboxOverride("pressed", RowBox(new Color(0.18f, 0.2f, 0.22f, 1f)));
        var captured = sel;
        row.Pressed += () => OnDatasetFlyoutRowPressed(captured);
        return row;
    }

    private static void OnDatasetFlyoutRowPressed(TelemetryDatasetSelection sel)
    {
        CloseScopeFlyout();
        TelemetryDatasetUiState.Selection = sel;
        if (_datasetButton is not null)
            _datasetButton.Text = FormatDatasetButtonCaption();
        SyncDrillTargetButtons();
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void PositionDatasetFlyoutPanel()
    {
        if (_datasetButton is null || _datasetFlyoutPanel is null || _layer is null)
            return;
        var r = _datasetButton.GetGlobalRect();
        var pos = new Vector2(r.Position.X, r.Position.Y + r.Size.Y + 2f);
        var menuSize = _datasetFlyoutPanel.GetCombinedMinimumSize();
        var vp = _layer.GetViewport();
        if (vp is not null)
        {
            var vr = vp.GetVisibleRect();
            if (pos.Y + menuSize.Y > vr.Position.Y + vr.Size.Y)
                pos.Y = Mathf.Max(vr.Position.Y + 4f, r.Position.Y - menuSize.Y - 2f);
            if (pos.X + menuSize.X > vr.Position.X + vr.Size.X)
                pos.X = vr.Position.X + vr.Size.X - menuSize.X - 8f;
            if (pos.X < vr.Position.X + 4f)
                pos.X = vr.Position.X + 4f;
        }

        _datasetFlyoutPanel.GlobalPosition = pos;
        _datasetFlyoutPanel.Size = menuSize;
    }

    private static void PositionActFlyoutPanel()
    {
        if (_actTargetButton is null || _actFlyoutPanel is null || _layer is null)
            return;
        var r = _actTargetButton.GetGlobalRect();
        var pos = new Vector2(r.Position.X, r.Position.Y + r.Size.Y + 2f);
        var menuSize = _actFlyoutPanel.GetCombinedMinimumSize();
        var vp = _layer.GetViewport();
        if (vp is not null)
        {
            var vr = vp.GetVisibleRect();
            if (pos.Y + menuSize.Y > vr.Position.Y + vr.Size.Y)
                pos.Y = Mathf.Max(vr.Position.Y + 4f, r.Position.Y - menuSize.Y - 2f);
            if (pos.X + menuSize.X > vr.Position.X + vr.Size.X)
                pos.X = vr.Position.X + vr.Size.X - menuSize.X - 8f;
            if (pos.X < vr.Position.X + 4f)
                pos.X = vr.Position.X + 4f;
        }

        _actFlyoutPanel.GlobalPosition = pos;
        _actFlyoutPanel.Size = menuSize;
    }

    private static void PositionCombatFlyoutPanel()
    {
        if (_combatTargetButton is null || _combatFlyoutPanel is null || _layer is null)
            return;
        var r = _combatTargetButton.GetGlobalRect();
        var pos = new Vector2(r.Position.X, r.Position.Y + r.Size.Y + 2f);
        var menuSize = _combatFlyoutPanel.GetCombinedMinimumSize();
        var vp = _layer.GetViewport();
        if (vp is not null)
        {
            var vr = vp.GetVisibleRect();
            if (pos.Y + menuSize.Y > vr.Position.Y + vr.Size.Y)
                pos.Y = Mathf.Max(vr.Position.Y + 4f, r.Position.Y - menuSize.Y - 2f);
            if (pos.X + menuSize.X > vr.Position.X + vr.Size.X)
                pos.X = vr.Position.X + vr.Size.X - menuSize.X - 8f;
            if (pos.X < vr.Position.X + 4f)
                pos.X = vr.Position.X + 4f;
        }

        _combatFlyoutPanel.GlobalPosition = pos;
        _combatFlyoutPanel.Size = menuSize;
    }

    private static void PositionScopeFlyoutPanel()
    {
        if (_scopeButton is null || _scopeFlyoutPanel is null || _layer is null)
            return;
        var r = _scopeButton.GetGlobalRect();
        var pos = new Vector2(r.Position.X, r.Position.Y + r.Size.Y + 2f);
        var menuSize = _scopeFlyoutPanel.GetCombinedMinimumSize();
        var vp = _layer.GetViewport();
        if (vp is not null)
        {
            var vr = vp.GetVisibleRect();
            if (pos.Y + menuSize.Y > vr.Position.Y + vr.Size.Y)
                pos.Y = Mathf.Max(vr.Position.Y + 4f, r.Position.Y - menuSize.Y - 2f);
            if (pos.X + menuSize.X > vr.Position.X + vr.Size.X)
                pos.X = vr.Position.X + vr.Size.X - menuSize.X - 8f;
            if (pos.X < vr.Position.X + 4f)
                pos.X = vr.Position.X + 4f;
        }

        _scopeFlyoutPanel.GlobalPosition = pos;
        _scopeFlyoutPanel.Size = menuSize;
    }

    private static void CloseScopeFlyout()
    {
        if (_scopeFlyoutPanel is not null)
            _scopeFlyoutPanel.Visible = false;
        if (_datasetFlyoutPanel is not null)
            _datasetFlyoutPanel.Visible = false;
        if (_actFlyoutPanel is not null)
            _actFlyoutPanel.Visible = false;
        if (_combatFlyoutPanel is not null)
            _combatFlyoutPanel.Visible = false;
        if (_flyoutLayer is not null)
            _flyoutLayer.Visible = false;
    }

    private static void OnScopeFlyoutBlockerGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            CloseScopeFlyout();
    }

    private static void OnScopeFlyoutRowPressed(int index)
    {
        CloseScopeFlyout();
        if (_scopeButton is null)
            return;
        _scopeIndex = Mathf.Clamp(index, 0, ScopeLabels.Length - 1);
        _scopeButton.Text = FormatScopeButtonCaption(_scopeIndex);
        SyncDrillTargetButtons();
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    /// <summary>Call once after the root is <see cref="Node.AddChild"/> to the scene tree.</summary>
    public static void OnAddedToTree()
    {
        if (_layer is null)
            return;

        var vp = _layer.GetViewport();
        if (vp is not null && !_viewportSizeHooked)
        {
            _viewportSizeHooked = true;
            vp.SizeChanged += OnViewportSizeChanged;
        }

        ApplyHostToViewport();
        Callable.From(ApplyHostToViewport).CallDeferred();
        Callable.From(ApplyMetricsPanelLayout).CallDeferred();
        Callable.From(() => RefreshMetricsPane(scrollMetricsToTop: true)).CallDeferred();

        MainFile.Logger.Info("AnalyticsTelemetry: TelemetryDebugOverlay UI ready (engine nodes only).");
        GD.Print("[AnalyticsTelemetry] TelemetryDebugOverlay _Ready");
    }

    private static void OnViewportSizeChanged() => ApplyHostToViewport();

    private static void ApplyHostToViewport()
    {
        if (_layer is null)
            return;
        var rect = _layer.GetViewport().GetVisibleRect();
        if (rect.Size.X <= 1f || rect.Size.Y <= 1f)
            return;
        if (_flyoutHost is not null)
        {
            _flyoutHost.CustomMinimumSize = rect.Size;
            _flyoutHost.Size = rect.Size;
        }
    }

    private static void TogglePanel()
    {
        if (_panel is null)
            return;
        _panel.Visible = !_panel.Visible;
        if (!_panel.Visible)
            CloseScopeFlyout();
        if (_panel.Visible)
            Callable.From(() => RefreshMetricsPane(scrollMetricsToTop: true)).CallDeferred();
    }

    private static void ApplyMetricsPanelLayout()
    {
        if (_panel is null || _metricsScroll is null || _metricsVisualHost is null)
            return;
        if (TelemetryMetricsUiPreferences.CompactPanel)
        {
            _panel.SetAnchor(Side.Bottom, 0f, false);
            _panel.OffsetTop = 46f;
            _panel.OffsetBottom = 46f + 520f;
            _panel.OffsetLeft = -392f;
            _panel.OffsetRight = -8f;
            _panel.CustomMinimumSize = new Vector2(288, 200);
            _metricsScroll.CustomMinimumSize = new Vector2(0, 150);
            _metricsVisualHost.CustomMinimumSize = new Vector2(252, 120);
        }
        else
        {
            _panel.SetAnchor(Side.Bottom, 1f, false);
            _panel.OffsetTop = 46f;
            _panel.OffsetBottom = -12f;
            _panel.OffsetLeft = -648f;
            _panel.OffsetRight = -8f;
            _panel.CustomMinimumSize = new Vector2(320, 200);
            _metricsScroll.CustomMinimumSize = new Vector2(0, 260);
            _metricsVisualHost.CustomMinimumSize = new Vector2(280, 180);
        }
    }

    private static void OnCompactMetricsPanelToggled(bool pressed)
    {
        TelemetryMetricsUiPreferences.CompactPanel = pressed;
        TelemetryMetricsUiPreferences.SaveToDisk();
        ApplyMetricsPanelLayout();
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void OnSleekChartsToggled(bool pressed)
    {
        TelemetryMetricsUiPreferences.SleekCharts = pressed;
        TelemetryMetricsUiPreferences.SaveToDisk();
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void OnChartHoverToggled(bool pressed)
    {
        TelemetryMetricsUiPreferences.ChartHover = pressed;
        TelemetryMetricsUiPreferences.SaveToDisk();
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void OnShowLiveThroughputChartToggled(bool pressed)
    {
        TelemetryMetricsUiPreferences.ShowLiveThroughputChart = pressed;
        TelemetryMetricsUiPreferences.SaveToDisk();
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    private static void OnDamageChartSeriesToggled(bool _)
    {
        if (_dmgChartInCheck is null || _dmgChartOutCheck is null || _dmgChartUnkCheck is null || _dmgChartBlockCheck is null)
            return;
        TelemetryMetricsUiPreferences.ShowChartDamageIn = _dmgChartInCheck.ButtonPressed;
        TelemetryMetricsUiPreferences.ShowChartDamageOut = _dmgChartOutCheck.ButtonPressed;
        TelemetryMetricsUiPreferences.ShowChartDamageUnk = _dmgChartUnkCheck.ButtonPressed;
        TelemetryMetricsUiPreferences.ShowChartBlock = _dmgChartBlockCheck.ButtonPressed;
        TelemetryMetricsUiPreferences.NormalizeDamageSeriesToggles();
        _dmgChartInCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageIn;
        _dmgChartOutCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageOut;
        _dmgChartUnkCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageUnk;
        _dmgChartBlockCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartBlock;
        TelemetryMetricsUiPreferences.SaveToDisk();
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: false);
    }

    private static void OnOverlayDisplayToggled(bool _pressed)
    {
        _lastChartTextureSignature = "\0";
        _lastVolatileUiSignature = "\0";
        RefreshMetricsPane(scrollMetricsToTop: true);
        _lastLogText = "";
    }

    private static void OnRecentEventsToggled(bool pressed)
    {
        ApplyRecentEventsVisibility(pressed);
        _lastLogText = "";
        if (_logLabel is not null)
            _logLabel.Text = "";
    }

    private static void ApplyRecentEventsVisibility(bool visible)
    {
        if (_logCaption is not null)
            _logCaption.Visible = visible;
        if (_logLabel is not null)
        {
            _logLabel.Visible = visible;
            _logLabel.CustomMinimumSize = visible ? new Vector2(0, 140) : new Vector2(0, 0);
        }
    }

    private static void RefreshMetricsPane(bool scrollMetricsToTop)
    {
        if (_metricsVisualHost is null)
            return;

        var now = Time.GetTicksMsec();
        if (!scrollMetricsToTop && now < _metricsRefreshEarliestTicksMsec)
            return;
        if (!scrollMetricsToTop)
            _metricsRefreshEarliestTicksMsec = now + MetricsPaneMinRefreshIntervalMsec;

        var drill = (MetricsDrillView)Math.Clamp(_scopeIndex, 0, 5);
        var model = TelemetryMetricsStore.BuildVisualModel(drill, TelemetryDatasetUiState.Selection);
        var includeDetail = _showFullDetailCheck?.ButtonPressed == true;
        var combatTargetSig = drill == MetricsDrillView.Combat
            ? TelemetryCombatUiState.SelectedCombatOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "live"
            : "-";
        var uiSig =
            $"{TelemetryMetricsUiPreferences.CompactPanel}|{TelemetryMetricsUiPreferences.SleekCharts}|{TelemetryMetricsUiPreferences.ChartHover}|{TelemetryMetricsUiPreferences.ShowLiveThroughputChart}" +
            $"|dI{TelemetryMetricsUiPreferences.ShowChartDamageIn}|dO{TelemetryMetricsUiPreferences.ShowChartDamageOut}|dU{TelemetryMetricsUiPreferences.ShowChartDamageUnk}|dB{TelemetryMetricsUiPreferences.ShowChartBlock}" +
            $"|ctd{combatTargetSig}";
        var chartSig = model.ChartTextureSignature() + "|" + uiSig;
        var volSig = model.VolatileUiSignature(includeDetail);
        if (chartSig == _lastChartTextureSignature && volSig == _lastVolatileUiSignature && !scrollMetricsToTop)
        {
            SyncDrillTargetButtons();
            return;
        }

        if (scrollMetricsToTop || chartSig != _lastChartTextureSignature)
        {
            _lastChartTextureSignature = chartSig;
            _lastVolatileUiSignature = volSig;
            MetricsVisualPanelFactory.Rebuild(
                _metricsVisualHost,
                model,
                compactDetail: !includeDetail,
                TelemetryMetricsUiPreferences.PresentationOptions);
        }
        else if (volSig != _lastVolatileUiSignature)
        {
            if (!MetricsVisualPanelFactory.TryRefreshVolatileOnly(
                    _metricsVisualHost,
                    model,
                    compactDetail: !includeDetail,
                    TelemetryMetricsUiPreferences.PresentationOptions))
            {
                _lastChartTextureSignature = chartSig;
                _lastVolatileUiSignature = volSig;
                MetricsVisualPanelFactory.Rebuild(
                    _metricsVisualHost,
                    model,
                    compactDetail: !includeDetail,
                    TelemetryMetricsUiPreferences.PresentationOptions);
            }
            else
                _lastVolatileUiSignature = volSig;
        }

        SyncDrillTargetButtons();
        if (scrollMetricsToTop)
            Callable.From(ScrollMetricsScrollToTop).CallDeferred();
    }

    private static void ScrollMetricsScrollToTop()
    {
        if (_metricsScroll is null)
            return;
        _metricsScroll.ScrollVertical = 0;
    }

    private static void OnClearHistoricMetricsPressed()
    {
        TelemetryMetricsStore.ClearAllHistoricMetrics();
        TelemetryEventLog.ClearRecentUiLines();
        _lastChartTextureSignature = "";
        _lastVolatileUiSignature = "";
        _lastLogText = "";
        if (_logLabel is not null)
            _logLabel.Text = "";
        RefreshMetricsPane(scrollMetricsToTop: true);
    }

    public static void TickUpdateRecentLines()
    {
        if (_panel is null || !_panel.Visible)
            return;

        RefreshMetricsPane(scrollMetricsToTop: false);

        if (_showRecentEventsCheck?.ButtonPressed != true || _logLabel is null || !_logLabel.Visible)
            return;

        var raw = _rawNdjsonCheck?.ButtonPressed == true;
        var lines = TelemetryEventLog.GetRecentLinesForDisplay(raw);
        var joined = string.Join("\n", lines);
        if (joined != _lastLogText)
        {
            _lastLogText = joined;
            _logLabel.Text = joined;
        }
    }
}
