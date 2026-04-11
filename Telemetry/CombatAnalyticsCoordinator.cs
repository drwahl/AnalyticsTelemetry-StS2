using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat.History.Entries;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Derives higher-level analytics from combat history stream (TTK, running pile counters).
/// Hand/deck snapshots require separate state hooks — see class remarks.
/// </summary>
/// <remarks>
/// <para><b>Time to kill</b> uses <see cref="DamageReceivedEntry"/> when we can read a victim key and detect
/// lethal HP from public properties. Anchor = first damage seen for that victim this session.</para>
/// <para><b>Session counters</b> (draw/discard/exhaust/generate) are <i>session-wide</i> until we add a reliable
/// per-combat boundary signal (e.g. explicit combat-start entry or room transition patch).</para>
/// <para><b>Not yet implemented</b> (need non–history hooks): attack/skill/power counts in hand, deck size at
/// combat start, exact pile sizes per turn.</para>
/// </remarks>
internal static class CombatAnalyticsCoordinator
{
    private static readonly ConcurrentDictionary<string, DateTime> FirstDamageUtcByVictim = new(StringComparer.Ordinal);

    private static long _drawn;
    private static long _discarded;
    private static long _exhausted;
    private static long _generated;

    internal static SessionCountersSnapshot? SessionCountersForEntryType(Type entryType)
    {
        if (entryType == typeof(CardDrawnEntry))
        {
            Interlocked.Increment(ref _drawn);
            return Snapshot();
        }

        if (entryType == typeof(CardDiscardedEntry))
        {
            Interlocked.Increment(ref _discarded);
            return Snapshot();
        }

        if (entryType == typeof(CardExhaustedEntry))
        {
            Interlocked.Increment(ref _exhausted);
            return Snapshot();
        }

        if (entryType == typeof(CardGeneratedEntry))
        {
            Interlocked.Increment(ref _generated);
            return Snapshot();
        }

        return null;
    }

    private static SessionCountersSnapshot Snapshot() =>
        new(Volatile.Read(ref _drawn), Volatile.Read(ref _discarded), Volatile.Read(ref _exhausted), Volatile.Read(ref _generated));

    /// <summary>Call after logging the damage entry itself.</summary>
    internal static void OnDamageReceivedEntry(DateTime occurredUtc, Dictionary<string, string?> props)
    {
        var victim = GuessVictimKey(props);
        if (string.IsNullOrEmpty(victim))
            return;

        FirstDamageUtcByVictim.TryAdd(victim, occurredUtc);

        if (!TryParseLethal(props, out var summary))
            return;

        FirstDamageUtcByVictim.TryGetValue(victim, out var first);
        double? ttk = first == default ? null : (occurredUtc - first).TotalSeconds;

        TelemetryEventLog.WriteRaw(
            "combat_enemy_defeated",
            new CombatEnemyDefeatedPayload(victim, ttk, summary, props),
            occurredUtc);

        FirstDamageUtcByVictim.TryRemove(victim, out _);
    }

    private static string? GuessVictimKey(Dictionary<string, string?> props)
    {
        foreach (var key in VictimPropertyCandidates)
        {
            if (props.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static readonly string[] VictimPropertyCandidates =
    [
        "Target", "Victim", "Creature", "DamagedCreature", "Receiver", "Subject",
    ];

    private static bool TryParseLethal(Dictionary<string, string?> props, out string? summary)
    {
        summary = null;
        foreach (var hpKey in RemainingHpPropertyCandidates)
        {
            if (!props.TryGetValue(hpKey, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, null, out var hp) && hp <= 0)
            {
                summary = $"lethal via {hpKey}={hp}";
                return true;
            }
        }

        foreach (var deathKey in DeathFlagCandidates)
        {
            if (props.TryGetValue(deathKey, out var raw)
                && bool.TryParse(raw, out var died)
                && died)
            {
                summary = $"lethal via {deathKey}=True";
                return true;
            }
        }

        if (props.TryGetValue("HumanReadableString", out var hr) && hr is not null)
        {
            var lower = hr.ToLowerInvariant();
            if (lower.Contains("died", StringComparison.Ordinal)
                || lower.Contains("slain", StringComparison.Ordinal)
                || lower.Contains("defeated", StringComparison.Ordinal))
            {
                summary = "lethal via HumanReadableString heuristic";
                return true;
            }
        }

        return false;
    }

    private static readonly string[] RemainingHpPropertyCandidates =
    [
        "RemainingHp", "HpAfter", "NewHp", "CurrentHp", "HealthAfter", "HitPointsAfter", "Hp",
    ];

    private static readonly string[] DeathFlagCandidates =
    [
        "Died", "WasKilled", "IsDead", "Lethal", "WasFatal", "Defeated",
    ];
}

public sealed record SessionCountersSnapshot(
    [property: JsonPropertyName("drawn")] long Drawn,
    [property: JsonPropertyName("discarded")] long Discarded,
    [property: JsonPropertyName("exhausted")] long Exhausted,
    [property: JsonPropertyName("generated")] long Generated);

public sealed record CombatEnemyDefeatedPayload(
    [property: JsonPropertyName("victimKey")] string? VictimKey,
    [property: JsonPropertyName("timeToKillSeconds")] double? TimeToKillSeconds,
    [property: JsonPropertyName("lethalSummary")] string? LethalSummary,
    [property: JsonPropertyName("damageEntryProperties")] Dictionary<string, string?> DamageEntryProperties);
