using AnalyticsTelemetry.AnalyticsTelemetryCode;
using AnalyticsTelemetry.Telemetry.Export;
using BaseLib.Config;
using Godot;
using FilePath = System.IO.Path;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// BaseLib Mod Config entry (Settings → Mods → Analytics Telemetry): deeper metrics than the in-run overlay.
/// </summary>
internal sealed class AnalyticsTelemetryModConfig : SimpleModConfig
{
    /// <summary>
    /// BaseLib skips <see cref="BaseLib.Config.ModConfigRegistry.Register"/> when <see cref="BaseLib.Config.ModConfig.HasSettings"/>
    /// is false (no persisted static properties). This mod uses a fully custom <see cref="SetupConfigUI"/> and does not
    /// surface this value in the UI.
    /// </summary>
    public static bool RegistrySentinel { get; set; }

    private OptionButton? _drilldown;
    private OptionButton? _datasetDropdown;
    private OptionButton? _actTargetDropdown;
    private OptionButton? _combatTargetDropdown;
    private bool _datasetDropdownProgrammatic;
    private bool _actTargetDropdownProgrammatic;
    private bool _combatTargetDropdownProgrammatic;
    private string _datasetFileListSig = "\0";
    private string _actKeysSig = "\0";
    private string _combatOrdinalsSig = "\0";
    private ScrollContainer? _liveTabScroll;
    private Control? _metricsVisualHost;
    private string _lastLiveChartTextureSignature = "";
    private string _lastLiveChartLayoutSignature = "";
    private ulong _nextLiveChartTextureEligibleTicksMsec;
    private string _lastLiveVolatileUiSignature = "";
    private ItemList? _fileList;
    private RichTextLabel? _fileTailBody;
    private Label? _activePathLabel;
    private Godot.Timer? _refreshTimer;
    private CheckButton? _metricsCompactChartsCheck;
    private CheckButton? _metricsSleekChartsCheck;
    private CheckButton? _metricsChartHoverCheck;
    private CheckButton? _metricsShowLiveThroughputCheck;
    private CheckButton? _metricsDmgInCheck;
    private CheckButton? _metricsDmgOutCheck;
    private CheckButton? _metricsDmgUnkCheck;
    private CheckButton? _metricsDmgBlockCheck;
    private CheckButton? _exportEnabledCheck;
    private OptionButton? _exportBackendDropdown;
    private LineEdit? _exportUrlEdit;
    private LineEdit? _exportAuthEdit;
    private LineEdit? _exportMeasurementEdit;
    private SpinBox? _exportBatchMaxSpin;
    private SpinBox? _exportBatchMsSpin;
    private Label? _exportStatusLabel;

    public override void SetupConfigUI(Control parent)
    {
        var tabs = new TabContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(680, 480),
        };

        tabs.AddChild(BuildLiveTab());
        tabs.AddChild(BuildSessionsTab());
        tabs.AddChild(BuildExportTab());

        parent.AddChild(tabs);
        AttachRefreshTimer(parent);
    }

    private Control BuildLiveTab()
    {
        var root = new MarginContainer { Name = "Live metrics" };
        root.AddThemeConstantOverride("margin_left", 4);
        root.AddThemeConstantOverride("margin_right", 4);
        root.AddThemeConstantOverride("margin_top", 4);
        root.AddThemeConstantOverride("margin_bottom", 4);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _liveTabScroll = scroll;
        root.AddChild(scroll);

        var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(v);

        v.AddChild(new Label
        {
            Text = "Same data as the in-run overlay: bar charts, counters, and full text. Updates while this tab is open.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        var clearMetricsBtn = new Button
        {
            Text = "Clear chart & counter memory",
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText =
                "Resets live charts, counters, hand buffer, replay aggregate cache, and the rolling event preview. "
                + "Does not delete NDJSON session files on disk.",
        };
        clearMetricsBtn.Pressed += OnClearHistoricMetricsPressed;
        v.AddChild(clearMetricsBtn);

        _drilldown = new OptionButton();
        _drilldown.AddItem("Overview");
        _drilldown.AddItem("Run (session file)");
        _drilldown.AddItem("Act");
        _drilldown.AddItem("Combat");
        _drilldown.AddItem("Hands");
        _drilldown.AddItem("Multiplayer");
        _drilldown.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _drilldown.ItemSelected += _ => RefreshLiveMetrics(scrollToTop: true);
        v.AddChild(_drilldown);

        _datasetDropdown = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _datasetDropdown.ItemSelected += OnDatasetDropdownItemSelected;
        v.AddChild(_datasetDropdown);
        RebuildDatasetDropdown();

        _actTargetDropdown = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Disabled = true,
        };
        _actTargetDropdown.ItemSelected += OnActTargetDropdownItemSelected;
        v.AddChild(_actTargetDropdown);

        _combatTargetDropdown = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Disabled = true,
        };
        _combatTargetDropdown.ItemSelected += OnCombatTargetDropdownItemSelected;
        v.AddChild(_combatTargetDropdown);

        _activePathLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _activePathLabel.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(_activePathLabel);

        TelemetryMetricsUiPreferences.LoadFromDisk();
        _metricsCompactChartsCheck = new CheckButton
        {
            Text = "Compact charts (smaller plots; same as overlay compact)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsCompactChartsCheck.ButtonPressed = TelemetryMetricsUiPreferences.CompactPanel;
        _metricsCompactChartsCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsCompactChartsCheck);

        _metricsSleekChartsCheck = new CheckButton
        {
            Text = "Sleek chart style",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsSleekChartsCheck.ButtonPressed = TelemetryMetricsUiPreferences.SleekCharts;
        _metricsSleekChartsCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsSleekChartsCheck);

        _metricsChartHoverCheck = new CheckButton
        {
            Text = "Chart hover values",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsChartHoverCheck.ButtonPressed = TelemetryMetricsUiPreferences.ChartHover;
        _metricsChartHoverCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsChartHoverCheck);

        _metricsShowLiveThroughputCheck = new CheckButton
        {
            Text = "Show live throughput chart (debug)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsShowLiveThroughputCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowLiveThroughputChart;
        _metricsShowLiveThroughputCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsShowLiveThroughputCheck);

        var dmgCap = new Label
        {
            Text = "Dmg in/out/block charts — show:",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        dmgCap.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(dmgCap);
        _metricsDmgInCheck = new CheckButton
        {
            Text = "Dmg in (to player)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsDmgInCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageIn;
        _metricsDmgInCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsDmgInCheck);
        _metricsDmgOutCheck = new CheckButton
        {
            Text = "Dmg out (to enemies)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsDmgOutCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageOut;
        _metricsDmgOutCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsDmgOutCheck);
        _metricsDmgUnkCheck = new CheckButton
        {
            Text = "Dmg unclassified",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsDmgUnkCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageUnk;
        _metricsDmgUnkCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsDmgUnkCheck);
        _metricsDmgBlockCheck = new CheckButton
        {
            Text = "Block",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _metricsDmgBlockCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartBlock;
        _metricsDmgBlockCheck.Toggled += OnMetricsChartPresentationToggled;
        v.AddChild(_metricsDmgBlockCheck);

        _metricsVisualHost = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(620, 400),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        v.AddChild(_metricsVisualHost);

        Callable.From(() => RefreshLiveMetrics(scrollToTop: true)).CallDeferred();
        return root;
    }

    private void OnMetricsChartPresentationToggled(bool _)
    {
        if (_metricsCompactChartsCheck is not null)
            TelemetryMetricsUiPreferences.CompactPanel = _metricsCompactChartsCheck.ButtonPressed;
        if (_metricsSleekChartsCheck is not null)
            TelemetryMetricsUiPreferences.SleekCharts = _metricsSleekChartsCheck.ButtonPressed;
        if (_metricsChartHoverCheck is not null)
            TelemetryMetricsUiPreferences.ChartHover = _metricsChartHoverCheck.ButtonPressed;
        if (_metricsShowLiveThroughputCheck is not null)
            TelemetryMetricsUiPreferences.ShowLiveThroughputChart = _metricsShowLiveThroughputCheck.ButtonPressed;
        if (_metricsDmgInCheck is not null)
            TelemetryMetricsUiPreferences.ShowChartDamageIn = _metricsDmgInCheck.ButtonPressed;
        if (_metricsDmgOutCheck is not null)
            TelemetryMetricsUiPreferences.ShowChartDamageOut = _metricsDmgOutCheck.ButtonPressed;
        if (_metricsDmgUnkCheck is not null)
            TelemetryMetricsUiPreferences.ShowChartDamageUnk = _metricsDmgUnkCheck.ButtonPressed;
        if (_metricsDmgBlockCheck is not null)
            TelemetryMetricsUiPreferences.ShowChartBlock = _metricsDmgBlockCheck.ButtonPressed;
        TelemetryMetricsUiPreferences.NormalizeDamageSeriesToggles();
        if (_metricsDmgInCheck is not null)
            _metricsDmgInCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageIn;
        if (_metricsDmgOutCheck is not null)
            _metricsDmgOutCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageOut;
        if (_metricsDmgUnkCheck is not null)
            _metricsDmgUnkCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartDamageUnk;
        if (_metricsDmgBlockCheck is not null)
            _metricsDmgBlockCheck.ButtonPressed = TelemetryMetricsUiPreferences.ShowChartBlock;
        TelemetryMetricsUiPreferences.SaveToDisk();
        BustLiveMetricsSignatures();
        RefreshLiveMetrics(scrollToTop: true);
    }

    private void BustLiveMetricsSignatures()
    {
        _lastLiveChartTextureSignature = "";
        _lastLiveChartLayoutSignature = "";
        _nextLiveChartTextureEligibleTicksMsec = 0;
        _lastLiveVolatileUiSignature = "";
    }

    private void OnClearHistoricMetricsPressed()
    {
        TelemetryMetricsStore.ClearAllHistoricMetrics();
        TelemetryEventLog.ClearRecentUiLines();
        BustLiveMetricsSignatures();
        RefreshLiveMetrics(scrollToTop: true);
    }

    private void RebuildDatasetDropdown()
    {
        if (_datasetDropdown is null)
            return;
        var files = TelemetryDatasetCatalog.ListSessions(32);
        _datasetFileListSig = string.Join("|", files.Select(f => f.FileName));
        _datasetDropdown.Clear();
        _actKeysSig = "\0";
        _combatOrdinalsSig = "\0";
        _datasetDropdown.AddItem("Current session (live)");
        _datasetDropdown.AddItem("All saved sessions");
        _datasetDropdown.AddItem("Last 24 hours");
        _datasetDropdown.AddItem("Last 7 days");
        _datasetDropdown.AddItem("Last 30 days");
        _datasetDropdown.AddItem("Last 365 days");
        foreach (var f in files)
            _datasetDropdown.AddItem($"Run: {f.FileName}");

        SyncDatasetDropdownSelected(files);
    }

    private void SyncDatasetDropdownSelected(IReadOnlyList<TelemetryDatasetCatalog.SessionDescriptor> files)
    {
        if (_datasetDropdown is null)
            return;
        var idx = TelemetryDatasetCatalog.DropdownIndexFromSelection(TelemetryDatasetUiState.Selection, files);
        idx = (int)Mathf.Clamp(idx, 0, _datasetDropdown.ItemCount - 1);
        if (_datasetDropdown.Selected == idx)
            return;
        _datasetDropdownProgrammatic = true;
        _datasetDropdown.Select(idx);
        _datasetDropdownProgrammatic = false;
    }

    private void OnDatasetDropdownItemSelected(long index)
    {
        if (_datasetDropdownProgrammatic)
            return;
        var files = TelemetryDatasetCatalog.ListSessions(32);
        TelemetryDatasetUiState.Selection = TelemetryDatasetCatalog.SelectionFromDropdownIndex((int)index, files);
        _actKeysSig = "\0";
        _combatOrdinalsSig = "\0";
        RefreshLiveMetrics(scrollToTop: true);
    }

    private void OnCombatTargetDropdownItemSelected(long index)
    {
        if (_combatTargetDropdownProgrammatic || _combatTargetDropdown is null)
            return;
        var meta = _combatTargetDropdown.GetItemMetadata((int)index);
        var s = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
        if (string.IsNullOrEmpty(s))
            TelemetryCombatUiState.SelectedCombatOrdinal = null;
        else if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var co) && co > 0)
            TelemetryCombatUiState.SelectedCombatOrdinal = co;
        RefreshLiveMetrics(scrollToTop: true);
    }

    private void OnActTargetDropdownItemSelected(long index)
    {
        if (_actTargetDropdownProgrammatic || _actTargetDropdown is null)
            return;
        var meta = _actTargetDropdown.GetItemMetadata((int)index);
        var s = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
        TelemetryActUiState.SelectedActKey = string.IsNullOrEmpty(s) ? null : s;
        RefreshLiveMetrics(scrollToTop: true);
    }

    private void EnsureActTargetDropdownUpToDate()
    {
        if (_actTargetDropdown is null)
            return;
        var keys = TelemetryMetricsStore.ListActKeysForUi(TelemetryDatasetUiState.Selection);
        var sig = string.Join("|", keys);
        if (sig != _actKeysSig)
        {
            _actKeysSig = sig;
            RebuildActTargetDropdownFromKeys(keys);
        }
        else
            SyncActTargetDropdownSelected();
    }

    private void RebuildActTargetDropdownFromKeys(IReadOnlyList<string> keys)
    {
        if (_actTargetDropdown is null)
            return;
        _actTargetDropdown.Clear();
        _actTargetDropdown.AddItem("Follow map act");
        _actTargetDropdown.SetItemMetadata(0, "");
        foreach (var k in keys)
        {
            var idx = _actTargetDropdown.ItemCount;
            _actTargetDropdown.AddItem(k);
            _actTargetDropdown.SetItemMetadata(idx, k);
        }

        SyncActTargetDropdownSelected();
    }

    private void SyncActTargetDropdownSelected()
    {
        if (_actTargetDropdown is null)
            return;
        var want = TelemetryActUiState.SelectedActKey ?? "";
        var found = -1;
        for (var i = 0; i < _actTargetDropdown.ItemCount; i++)
        {
            var meta = _actTargetDropdown.GetItemMetadata(i);
            var m = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
            if (m == want)
            {
                found = i;
                break;
            }
        }

        if (found < 0)
        {
            if (want.Length > 0)
                TelemetryActUiState.SelectedActKey = null;
            found = 0;
        }

        if (_actTargetDropdown.Selected == found)
            return;
        _actTargetDropdownProgrammatic = true;
        _actTargetDropdown.Select(found);
        _actTargetDropdownProgrammatic = false;
    }

    private void EnsureCombatTargetDropdownUpToDate()
    {
        if (_combatTargetDropdown is null)
            return;
        var ordinals = TelemetryMetricsStore.ListCombatOrdinalsForUi(TelemetryDatasetUiState.Selection);
        var sig = string.Join("|", ordinals);
        if (sig != _combatOrdinalsSig)
        {
            _combatOrdinalsSig = sig;
            RebuildCombatTargetDropdownFromOrdinals(ordinals);
        }
        else
            SyncCombatTargetDropdownSelected();
    }

    private void RebuildCombatTargetDropdownFromOrdinals(IReadOnlyList<int> ordinals)
    {
        if (_combatTargetDropdown is null)
            return;
        _combatTargetDropdown.Clear();
        _combatTargetDropdown.AddItem("Follow live combat");
        _combatTargetDropdown.SetItemMetadata(0, "");
        foreach (var n in ordinals)
        {
            var idx = _combatTargetDropdown.ItemCount;
            _combatTargetDropdown.AddItem($"Combat #{n}");
            _combatTargetDropdown.SetItemMetadata(idx, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        SyncCombatTargetDropdownSelected();
    }

    private void SyncCombatTargetDropdownSelected()
    {
        if (_combatTargetDropdown is null)
            return;
        var want = TelemetryCombatUiState.SelectedCombatOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var found = -1;
        for (var i = 0; i < _combatTargetDropdown.ItemCount; i++)
        {
            var meta = _combatTargetDropdown.GetItemMetadata(i);
            var m = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
            if (m == want)
            {
                found = i;
                break;
            }
        }

        if (found < 0)
        {
            if (want.Length > 0)
                TelemetryCombatUiState.SelectedCombatOrdinal = null;
            found = 0;
        }

        if (_combatTargetDropdown.Selected == found)
            return;
        _combatTargetDropdownProgrammatic = true;
        _combatTargetDropdown.Select(found);
        _combatTargetDropdownProgrammatic = false;
    }

    /// <summary>Attach after the tab container is in the tree (MarginContainer only accepts <see cref="Control"/> children).</summary>
    internal void AttachRefreshTimer(Control parent)
    {
        if (_refreshTimer is not null)
            return;
        _refreshTimer = new Godot.Timer
        {
            WaitTime = 0.4,
            Autostart = true,
            OneShot = false,
        };
        _refreshTimer.Timeout += () => RefreshLiveMetrics(scrollToTop: false);
        parent.AddChild(_refreshTimer);
    }

    private Control BuildSessionsTab()
    {
        var root = new MarginContainer { Name = "Session files" };
        root.AddThemeConstantOverride("margin_left", 4);
        root.AddThemeConstantOverride("margin_right", 4);
        root.AddThemeConstantOverride("margin_top", 4);
        root.AddThemeConstantOverride("margin_bottom", 4);

        var split = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(split);

        split.AddChild(new Label
        {
            Text = "Recent NDJSON sessions (newest first). Select a file to preview the tail.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        var clearFromSessionsTab = new Button
        {
            Text = "Clear chart & counter memory",
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText =
                "Same as Live metrics: clears in-process rollups and chart buffers. Does not delete files below.",
        };
        clearFromSessionsTab.Pressed += OnClearHistoricMetricsPressed;
        split.AddChild(clearFromSessionsTab);

        var h = new HSplitContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 400),
        };
        split.AddChild(h);

        _fileList = new ItemList
        {
            CustomMinimumSize = new Vector2(260, 200),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SameColumnWidth = true,
        };
        _fileList.ItemSelected += OnSessionFileSelected;
        h.AddChild(_fileList);

        var right = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        h.AddChild(right);

        _fileTailBody = new RichTextLabel
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(320, 200),
            FitContent = false,
            ScrollActive = true,
            ScrollFollowing = false,
            BbcodeEnabled = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        right.AddChild(_fileTailBody);

        Callable.From(PopulateSessionFileList).CallDeferred();
        return root;
    }

    private Control BuildExportTab()
    {
        TelemetryExportPreferences.LoadFromDisk();

        var root = new MarginContainer { Name = "Export" };
        root.AddThemeConstantOverride("margin_left", 4);
        root.AddThemeConstantOverride("margin_right", 4);
        root.AddThemeConstantOverride("margin_top", 4);
        root.AddThemeConstantOverride("margin_bottom", 4);

        var v = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(v);

        v.AddChild(new Label
        {
            Text =
                "Optional second sink: HTTP POST in InfluxDB line protocol (works with VictoriaMetrics /write, InfluxDB write APIs). " +
                "NDJSON file logging is unchanged. Add new backends via TelemetrySinkFactory + TelemetryExportKind.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        _exportEnabledCheck = new CheckButton
        {
            Text = "Enable remote export",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _exportEnabledCheck.ButtonPressed = TelemetryExportPreferences.RemoteEnabled;
        v.AddChild(_exportEnabledCheck);

        v.AddChild(new Label { Text = "Backend (extensible):" });
        _exportBackendDropdown = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _exportBackendDropdown.AddItem("Influx line protocol (HTTP POST)");
        _exportBackendDropdown.Select(0);
        v.AddChild(_exportBackendDropdown);

        v.AddChild(new Label { Text = "Write URL (must be http or https):" });
        _exportUrlEdit = new LineEdit
        {
            Text = TelemetryExportPreferences.InfluxWriteUrl,
            PlaceholderText = "http://127.0.0.1:8428/write",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        v.AddChild(_exportUrlEdit);

        v.AddChild(new Label { Text = "Authorization header (optional), e.g. Token <secret> for InfluxDB 2:" });
        _exportAuthEdit = new LineEdit
        {
            Text = TelemetryExportPreferences.AuthorizationHeaderValue ?? "",
            Secret = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        v.AddChild(_exportAuthEdit);

        v.AddChild(new Label { Text = "Measurement name (low cardinality):" });
        _exportMeasurementEdit = new LineEdit
        {
            Text = TelemetryExportPreferences.InfluxMeasurement,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        v.AddChild(_exportMeasurementEdit);

        var batchRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        batchRow.AddChild(new Label { Text = "Batch max lines" });
        _exportBatchMaxSpin = new SpinBox
        {
            MinValue = 1,
            MaxValue = 512,
            Step = 1,
            Value = TelemetryExportPreferences.BatchMaxLines,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        batchRow.AddChild(_exportBatchMaxSpin);
        batchRow.AddChild(new Label { Text = "Interval (ms)" });
        _exportBatchMsSpin = new SpinBox
        {
            MinValue = 100,
            MaxValue = 60_000,
            Step = 100,
            Value = TelemetryExportPreferences.BatchIntervalMs,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        batchRow.AddChild(_exportBatchMsSpin);
        v.AddChild(batchRow);

        var apply = new Button { Text = "Apply export settings" };
        apply.Pressed += OnExportApplyPressed;
        v.AddChild(apply);

        _exportStatusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _exportStatusLabel.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(_exportStatusLabel);
        RefreshExportStatusLabel();

        return root;
    }

    private void RefreshExportStatusLabel()
    {
        if (_exportStatusLabel is null)
            return;
        _exportStatusLabel.Text = TelemetryExportPreferences.RemoteEnabled && !string.IsNullOrWhiteSpace(TelemetryExportPreferences.InfluxWriteUrl)
            ? "Remote export: enabled (reload applied — check game log if writes fail)."
            : "Remote export: off or incomplete URL — only NDJSON is written.";
    }

    private void OnExportApplyPressed()
    {
        if (_exportEnabledCheck is null || _exportUrlEdit is null || _exportAuthEdit is null
            || _exportMeasurementEdit is null || _exportBatchMaxSpin is null || _exportBatchMsSpin is null)
            return;

        TelemetryExportPreferences.RemoteEnabled = _exportEnabledCheck.ButtonPressed;
        TelemetryExportPreferences.Kind = TelemetryExportKind.InfluxLineProtocolHttp;
        TelemetryExportPreferences.InfluxWriteUrl = _exportUrlEdit.Text?.Trim() ?? "";
        TelemetryExportPreferences.AuthorizationHeaderValue = string.IsNullOrWhiteSpace(_exportAuthEdit.Text)
            ? null
            : _exportAuthEdit.Text.Trim();
        var m = _exportMeasurementEdit.Text?.Trim() ?? "";
        TelemetryExportPreferences.InfluxMeasurement = string.IsNullOrEmpty(m) ? "analytics_telemetry" : m;
        TelemetryExportPreferences.BatchMaxLines = (int)_exportBatchMaxSpin.Value;
        TelemetryExportPreferences.BatchIntervalMs = (int)_exportBatchMsSpin.Value;
        TelemetryExportPreferences.SaveToDisk();
        TelemetryEventLog.ReloadRemoteSinksFromPreferences();
        MainFile.Logger.Info("AnalyticsTelemetry: export preferences saved; remote sink reloaded.");
        RefreshExportStatusLabel();
    }

    private void PopulateSessionFileList()
    {
        if (_fileList is null)
            return;
        _fileList.Clear();
        var entries = TelemetrySessionFiles.ListRecent(32);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            _fileList.AddItem($"{e.FileName}  —  {e.LastWriteUtc:yyyy-MM-dd HH:mm} UTC");
            _fileList.SetItemMetadata(i, e.FullPath);
        }

        if (entries.Count == 0)
            _fileList.AddItem("(no sessions yet — start a run with the mod enabled)");

        if (_fileTailBody is not null)
            _fileTailBody.Text = entries.Count == 0 ? "" : "Select a file.";
    }

    private void OnSessionFileSelected(long index)
    {
        if (_fileList is null || _fileTailBody is null)
            return;
        if (index < 0 || index >= _fileList.ItemCount)
            return;
        var meta = _fileList.GetItemMetadata((int)index);
        if (meta.VariantType != Variant.Type.String)
            return;
        var path = meta.AsString();
        if (string.IsNullOrEmpty(path))
            return;
        _fileTailBody.Text = TelemetrySessionFiles.ReadTail(path);
    }

    private void RefreshLiveMetrics(bool scrollToTop)
    {
        if (_metricsVisualHost is null || _drilldown is null)
            return;

        var files = TelemetryDatasetCatalog.ListSessions(32);
        var sig = string.Join("|", files.Select(f => f.FileName));
        if (sig != _datasetFileListSig && _datasetDropdown is not null)
            RebuildDatasetDropdown();
        else
            SyncDatasetDropdownSelected(files);

        var drill = (MetricsDrillView)Math.Clamp(_drilldown.Selected, 0, 5);
        if (_actTargetDropdown is not null)
        {
            _actTargetDropdown.Disabled = drill != MetricsDrillView.Act;
            if (drill == MetricsDrillView.Act)
                EnsureActTargetDropdownUpToDate();
        }

        if (_combatTargetDropdown is not null)
        {
            _combatTargetDropdown.Disabled = drill != MetricsDrillView.Combat;
            if (drill == MetricsDrillView.Combat)
                EnsureCombatTargetDropdownUpToDate();
        }

        var model = TelemetryMetricsStore.BuildVisualModel(drill, TelemetryDatasetUiState.Selection);
        var combatTargetSig = drill == MetricsDrillView.Combat
            ? TelemetryCombatUiState.SelectedCombatOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "live"
            : "-";
        var uiSig =
            $"{TelemetryMetricsUiPreferences.CompactPanel}|{TelemetryMetricsUiPreferences.SleekCharts}|{TelemetryMetricsUiPreferences.ChartHover}|{TelemetryMetricsUiPreferences.ShowLiveThroughputChart}" +
            $"|dI{TelemetryMetricsUiPreferences.ShowChartDamageIn}|dO{TelemetryMetricsUiPreferences.ShowChartDamageOut}|dU{TelemetryMetricsUiPreferences.ShowChartDamageUnk}|dB{TelemetryMetricsUiPreferences.ShowChartBlock}" +
            $"|ctd{combatTargetSig}";

        if (_activePathLabel is not null)
        {
            var p = TelemetryEventLog.SessionPath;
            _activePathLabel.Text =
                "Active NDJSON (this process): " + (p is null ? "(not started)" : FilePath.GetFileName(p))
                + "\nDataset uses " + (TelemetryDatasetUiState.Selection.Kind == TelemetryDatasetKind.CurrentSession
                    ? "live in-memory metrics."
                    : "replayed totals from disk (see chart subtitles).");
        }

        var chartSig = model.ChartTextureSignature() + "|" + uiSig;
        var chartLayoutSig = model.ChartLayoutSignature() + "|" + uiSig;
        var volSig = model.VolatileUiSignature(includeDetailText: true);
        var now = Time.GetTicksMsec();
        var plan = MetricsUiRefreshPolicy.DecideWithThrottledChartTextures(
            chartSig,
            chartLayoutSig,
            volSig,
            _lastLiveChartTextureSignature,
            _lastLiveChartLayoutSignature,
            _lastLiveVolatileUiSignature,
            scrollToTop,
            now,
            ref _nextLiveChartTextureEligibleTicksMsec);
        if (plan == MetricsUiRefreshKind.Noop)
            return;

        if (plan == MetricsUiRefreshKind.FullRebuild)
        {
            var chartDataOnly = MetricsUiRefreshPolicy.ShouldUseInPlaceChartTextureSwap(
                scrollToTop,
                chartLayoutSig,
                _lastLiveChartLayoutSignature,
                chartSig,
                _lastLiveChartTextureSignature,
                volSig,
                _lastLiveVolatileUiSignature);

            if (chartDataOnly
                && MetricsVisualPanelFactory.TrySwapTimeSeriesChartTextures(
                    _metricsVisualHost,
                    model,
                    compactDetail: false,
                    TelemetryMetricsUiPreferences.PresentationOptions))
            {
                _lastLiveChartTextureSignature = chartSig;
            }
            else
            {
                _lastLiveChartTextureSignature = chartSig;
                _lastLiveChartLayoutSignature = chartLayoutSig;
                _lastLiveVolatileUiSignature = volSig;
                MetricsVisualPanelFactory.Rebuild(
                    _metricsVisualHost,
                    model,
                    compactDetail: false,
                    TelemetryMetricsUiPreferences.PresentationOptions);
            }
        }
        else if (plan == MetricsUiRefreshKind.VolatileOnly)
        {
            if (!MetricsVisualPanelFactory.TryRefreshVolatileOnly(
                    _metricsVisualHost,
                    model,
                    compactDetail: false,
                    TelemetryMetricsUiPreferences.PresentationOptions,
                    out var rebuiltTail))
            {
                _lastLiveChartTextureSignature = chartSig;
                _lastLiveChartLayoutSignature = chartLayoutSig;
                _lastLiveVolatileUiSignature = volSig;
                MetricsVisualPanelFactory.Rebuild(
                    _metricsVisualHost,
                    model,
                    compactDetail: false,
                    TelemetryMetricsUiPreferences.PresentationOptions);
            }
            else if (rebuiltTail)
                _lastLiveVolatileUiSignature = volSig;
        }

        if (scrollToTop && _liveTabScroll is not null)
            Callable.From(() => _liveTabScroll.ScrollVertical = 0).CallDeferred();
    }
}
