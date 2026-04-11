using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Harmony postfixes on combat <see cref="MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry"/> constructors.
/// </summary>
public static class GameplayHarmonyPatches
{
    private static readonly Type[] TrackedEntryTypes =
    [
        typeof(CardPlayStartedEntry),
        typeof(CardPlayFinishedEntry),
        typeof(CardDrawnEntry),
        typeof(CardDiscardedEntry),
        typeof(CardExhaustedEntry),
        typeof(CardGeneratedEntry),
        typeof(CardAfflictedEntry),
        typeof(DamageReceivedEntry),
        typeof(CreatureAttackedEntry),
        typeof(MonsterPerformedMoveEntry),
        typeof(BlockGainedEntry),
        typeof(EnergySpentEntry),
        typeof(PowerReceivedEntry),
        typeof(PotionUsedEntry),
        typeof(OrbChanneledEntry),
        typeof(SummonedEntry),
        typeof(StarsModifiedEntry),
    ];

    [HarmonyPatch]
    public static class CombatHistoryEntryConstructorsPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in TrackedEntryTypes)
            {
                foreach (var c in t.GetConstructors(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    yield return c;
            }
        }

        public static void Postfix(object __instance)
        {
            var occ = DateTime.UtcNow;
            var t = __instance.GetType();
            var eventType = CombatHistoryEventNaming.EventTypeFor(t);
            CombatHistoryEntryTelemetry.Emit(eventType, __instance, occ);
        }
    }

    /// <summary>Canonical player energy mutations (gains, losses, absolute sets including turn reset).</summary>
    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
    public static class PlayerCmdGainEnergyPatch
    {
        public static void Postfix(decimal amount, Player player) =>
            CombatEnergyFlowTracker.OnPlayerCmdEnergy("gain", amount, player);
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseEnergy))]
    public static class PlayerCmdLoseEnergyPatch
    {
        public static void Postfix(decimal amount, Player player) =>
            CombatEnergyFlowTracker.OnPlayerCmdEnergy("lose", amount, player);
    }

    [HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.SetEnergy))]
    public static class PlayerCmdSetEnergyPatch
    {
        public static void Postfix(decimal amount, Player player) =>
            CombatEnergyFlowTracker.OnPlayerCmdEnergy("set", amount, player);
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.StartCombatInternal))]
    public static class CombatManagerStartInternalPatch
    {
        public static void Postfix()
        {
            TelemetryScopeContext.OnCombatStarted();
            var s = TelemetryScopeContext.Snapshot();
            TelemetryEventLog.WriteRaw(
                "combat_started",
                new CombatStartedPayload(
                    s.CombatOrdinal,
                    s.ActIndex,
                    s.ActId,
                    s.MapDepth,
                    s.RunMode,
                    s.PartyPlayerKeys));
        }
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal))]
    public static class CombatManagerEndInternalPatch
    {
        public static void Postfix()
        {
            var s = TelemetryScopeContext.Snapshot();
            var dur = s.CombatStartUtc is { } t ? (DateTime.UtcNow - t).TotalSeconds : (double?)null;
            TelemetryEventLog.WriteRaw(
                "combat_ended",
                new CombatEndedPayload(s.CombatOrdinal, dur));
            CardDamageAttributionTracker.OnCombatEnded(s.CombatOrdinal);
            TelemetryScopeContext.OnCombatEnded();
        }
    }
}

internal static class CombatHistoryEventNaming
{
    internal static string EventTypeFor(Type t)
    {
        var name = t.Name;
        if (name.EndsWith("Entry", StringComparison.Ordinal))
            name = name[..^5];
        return "combat_history_" + PascalToSnake(name);
    }

    private static string PascalToSnake(string p)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (i > 0 && char.IsUpper(c))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
