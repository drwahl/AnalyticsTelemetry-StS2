using System;
using System.Reflection;
using AnalyticsTelemetry.Telemetry;
using BaseLib.Config;
using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace AnalyticsTelemetry.AnalyticsTelemetryCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "AnalyticsTelemetry";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    private static bool _overlayEnsureHooked;

    /// <summary>Mod config UI is registered on the first <see cref="SceneTree.ProcessFrame"/> so BaseLib’s registry is ready (avoids races with immediate <see cref="Initialize"/>).</summary>
    private static bool _modConfigRegisterAttempted;

    /// <summary>Earliest <see cref="Time.GetTicksMsec"/> at which the debug overlay may attach (after splash).</summary>
    private static ulong? _overlayEarliestTicksMsec;

    /// <summary>Wait this long after the first <see cref="SceneTree.ProcessFrame"/> before showing the overlay.</summary>
    private const ulong OverlayAttachDelayAfterFirstFrameMs = 2500;

    public static void Initialize()
    {
        try
        {
            Telemetry.TelemetryDiagnostics.LogRuntimeAssemblies(Logger);
            Telemetry.TelemetryEventLog.Init(ModId);
            Telemetry.TelemetryMetricsUiPreferences.LoadFromDisk();
            Telemetry.GameplayHarmony.TryApply();
            Telemetry.RunSaveTelemetry.Start();
            var modVer = typeof(MainFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(MainFile).Assembly.GetName().Version?.ToString()
                ?? "?";
            Logger.Info($"AnalyticsTelemetry {modVer} initialized; NDJSON session started.");
        }
        catch (Exception e)
        {
            Logger.Error($"AnalyticsTelemetry log init failed: {e}");
        }

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            // Keep re-attaching if the game replaces root children (scene loads), so the overlay
            // survives main menu → run transitions and late tree setup.
            if (!_overlayEnsureHooked)
            {
                _overlayEnsureHooked = true;
                tree.ProcessFrame += OnTelemetryProcessFrame;
            }
        }
        else
        {
            Logger.Error("AnalyticsTelemetry: no SceneTree; UI overlay not attached.");
            TryRegisterModConfigOnce();
        }

        // Gameplay hooks: Harmony postfixes on combat history entry constructors (see Telemetry/*Harmony*).
        // BaseLib's ModHelper.SubscribeCombat is not callable from other assemblies (internal).
    }

    private static void OnTelemetryProcessFrame()
    {
        TryRegisterModConfigOnce();
        EnsureTelemetryOverlayPresent();
        Telemetry.TelemetryDebugOverlayUi.TickUpdateRecentLines();
    }

    private static void TryRegisterModConfigOnce()
    {
        if (_modConfigRegisterAttempted)
            return;
        _modConfigRegisterAttempted = true;
        try
        {
            ModConfigRegistry.Register(ModId, new AnalyticsTelemetryModConfig());
            Logger.Info("AnalyticsTelemetry: registered BaseLib Mod Config (Settings → Mods → metrics tabs).");
        }
        catch (Exception e)
        {
            Logger.Warn($"AnalyticsTelemetry: ModConfigRegistry.Register failed: {e}");
        }
    }

    private static void EnsureTelemetryOverlayPresent()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;

        var now = Time.GetTicksMsec();
        _overlayEarliestTicksMsec ??= now + OverlayAttachDelayAfterFirstFrameMs;
        if (now < _overlayEarliestTicksMsec)
            return;

        var root = tree.Root;
        if (root is null)
            return;

        if (root.GetNodeOrNull(Telemetry.TelemetryDebugOverlayUi.RootNodeName) is not null)
            return;

        var overlay = Telemetry.TelemetryDebugOverlayUi.CreateRoot();
        root.AddChild(overlay);
        Telemetry.TelemetryDebugOverlayUi.OnAddedToTree();
        Logger.Info("AnalyticsTelemetry: overlay parented to scene root.");
    }
}
