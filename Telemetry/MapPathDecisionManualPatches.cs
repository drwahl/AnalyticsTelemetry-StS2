using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Prefix bodies referenced by <see cref="GameplayHarmony"/> manual patch registration.</summary>
internal static class MapPathDecisionManualPatches
{
    internal static void VisibilityPrefixBool(bool visible) =>
        MapPathDecisionTelemetry.OnMapScreenVisibilityChanged(visible);

    /// <summary>Callback signature unknown — assume map became interactable.</summary>
    internal static void VisibilityPrefixNoArg() =>
        MapPathDecisionTelemetry.OnMapScreenVisibilityChanged(true);
}
