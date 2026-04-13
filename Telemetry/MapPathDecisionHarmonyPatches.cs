using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Prefix on <see cref="NMapScreen.TravelToMapCoord"/> — decision interval ends when travel starts.
/// Visibility start is applied manually in <see cref="GameplayHarmony"/> (see <c>TryApplyMapPathDecisionPatches</c>).
/// </summary>
[HarmonyPatch]
public static class NMapScreenTravelToMapCoordPatch
{
    private static readonly MethodBase? TravelMethod = FindTravelToMapCoord();

    static MethodBase TargetMethod() => TravelMethod!;

    static bool Prepare() => TravelMethod is not null;

    private static MethodBase? FindTravelToMapCoord()
    {
        foreach (var m in typeof(NMapScreen).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name == nameof(NMapScreen.TravelToMapCoord))
                return m;
        }

        return null;
    }

    public static void Prefix() => MapPathDecisionTelemetry.OnTravelToMapCoordInvoked();
}
