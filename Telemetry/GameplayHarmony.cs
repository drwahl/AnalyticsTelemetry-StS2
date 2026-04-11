using AnalyticsTelemetry.AnalyticsTelemetryCode;
using HarmonyLib;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Applies Harmony patches for gameplay telemetry (history entry ctors, <c>PlayerCmd</c> energy, <c>CombatManager</c> lifecycle).</summary>
public static class GameplayHarmony
{
    private const string HarmonyId = "AnalyticsTelemetry.Gameplay";
    private static bool _applied;

    public static void TryApply()
    {
        if (_applied)
            return;
        _applied = true;

        try
        {
            var h = new Harmony(HarmonyId);
            h.PatchAll(typeof(GameplayHarmony).Assembly);
            MainFile.Logger.Info("AnalyticsTelemetry: Harmony gameplay patches applied.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"AnalyticsTelemetry: Harmony gameplay patches failed: {e}");
        }
    }
}
