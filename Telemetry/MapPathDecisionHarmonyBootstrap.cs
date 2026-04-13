using System.Reflection;
using AnalyticsTelemetry.AnalyticsTelemetryCode;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Registers map path decision timing hooks that need runtime method resolution.</summary>
internal static class MapPathDecisionHarmonyBootstrap
{
    internal static void TryPatchVisibility(Harmony harmony)
    {
        foreach (var m in typeof(NMapScreen).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name != "OnMapScreenVisibilityChanged")
                continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            {
                harmony.Patch(
                    m,
                    prefix: new HarmonyMethod(
                        typeof(MapPathDecisionManualPatches),
                        nameof(MapPathDecisionManualPatches.VisibilityPrefixBool)));
                MainFile.Logger.Info("AnalyticsTelemetry: map_path_decision start = OnMapScreenVisibilityChanged(bool).");
                return;
            }

            if (ps.Length == 0)
            {
                harmony.Patch(
                    m,
                    prefix: new HarmonyMethod(
                        typeof(MapPathDecisionManualPatches),
                        nameof(MapPathDecisionManualPatches.VisibilityPrefixNoArg)));
                MainFile.Logger.Info("AnalyticsTelemetry: map_path_decision start = OnMapScreenVisibilityChanged() (treated as visible).");
                return;
            }
        }

        var notif = typeof(NMapScreen).GetMethod(
            "_Notification",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(long) },
            null);
        if (notif is not null)
        {
            harmony.Patch(
                notif,
                postfix: new HarmonyMethod(
                    typeof(MapPathDecisionNotificationPatch),
                    nameof(MapPathDecisionNotificationPatch.Postfix)));
            MainFile.Logger.Info("AnalyticsTelemetry: map_path_decision start = NMapScreen._Notification (visibility).");
            return;
        }

        MainFile.Logger.Info(
            "AnalyticsTelemetry: map_path_decision has no visibility hook; map_path_decision lines may be absent until game exposes one.");
    }
}

/// <summary>Godot visibility notification fallback when <c>OnMapScreenVisibilityChanged</c> is missing.</summary>
internal static class MapPathDecisionNotificationPatch
{
    public static void Postfix(NMapScreen __instance, long what)
    {
        if (what != (long)Godot.CanvasItem.NotificationVisibilityChanged)
            return;
        MapPathDecisionTelemetry.OnMapScreenVisibilityChanged(__instance.Visible);
    }
}
