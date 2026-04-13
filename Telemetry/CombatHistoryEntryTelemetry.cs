using System.Reflection;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat.History.Entries;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Flattens a <see cref="MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry"/> (or subtype) for logging.</summary>
internal static class CombatHistoryEntryTelemetry
{
    internal static void Emit(string eventType, object entry, DateTime occurredUtc)
    {
        var entryType = entry.GetType();
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in entryType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!p.CanRead)
                continue;
            try
            {
                var v = p.GetValue(entry);
                fields[p.Name] = v?.ToString();
            }
            catch
            {
                fields[p.Name] = "<unreadable>";
            }
        }

        var counters = CombatAnalyticsCoordinator.SessionCountersForEntryType(entryType);
        CombatHistoryAnalyticsAttachment? analytics =
            counters is null ? null : new CombatHistoryAnalyticsAttachment(counters);

        StatusEffectDerivation? statusEffect = null;
        if (CombatHistoryStatusEffectMetrics.TryDeriveFromDictionary(fields, entryType.Name, out var se))
            statusEffect = se;

        CardDamageAttributionTracker.ProcessEntry(entry, fields);

        if (entry is CardPlayStartedEntry)
            HandCardPlayOrderTracker.OnCardPlayStarted(fields, occurredUtc);

        TelemetryEventLog.WriteRaw(
            eventType,
            new CombatHistoryEntryPayload(entryType.Name, entry.ToString(), fields, analytics, statusEffect),
            occurredUtc);

        CombatTurnTimingTracker.OnHistoryEntryAfterLog(occurredUtc, entryType.Name, fields);

        if (CombatTurnTimingTracker.TryResolveSideRound(entryType.Name, fields, out var histSide, out _))
            CombatEnergyFlowTracker.NotifyHistorySide(histSide);

        if (entry is DamageReceivedEntry)
            CombatAnalyticsCoordinator.OnDamageReceivedEntry(occurredUtc, fields);
    }
}

/// <summary>Optional best-effort classification for powers / card afflictions (mirrors metric rollups).</summary>
public sealed record StatusEffectDerivation(
    [property: JsonPropertyName("lineKind")] string LineKind,
    [property: JsonPropertyName("recipient")] string Recipient,
    [property: JsonPropertyName("effectKey")] string EffectKey);

/// <summary>Payload for combat history hook events (property names mirror game objects where possible).</summary>
public sealed record CombatHistoryEntryPayload(
    [property: JsonPropertyName("entryType")] string EntryType,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("properties")] Dictionary<string, string?> Properties,
    [property: JsonPropertyName("analytics")] CombatHistoryAnalyticsAttachment? Analytics,
    [property: JsonPropertyName("statusEffect")] StatusEffectDerivation? StatusEffect = null);

public sealed record CombatHistoryAnalyticsAttachment(
    [property: JsonPropertyName("sessionCounters")] SessionCountersSnapshot SessionCounters);
