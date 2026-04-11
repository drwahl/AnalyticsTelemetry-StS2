using System.Globalization;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat.History.Entries;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Attributes <see cref="DamageReceivedEntry"/> amounts to the innermost active
/// <see cref="CardPlayStartedEntry"/> / <see cref="CardPlayFinishedEntry"/> bracket (per combat).
/// </summary>
internal static class CardDamageAttributionTracker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, List<string>> StackByCombat = new();

    internal static void ResetSession()
    {
        lock (Gate)
            StackByCombat.Clear();
    }

    internal static void OnCombatEnded(int combatOrdinal)
    {
        if (combatOrdinal <= 0)
            return;
        lock (Gate)
            StackByCombat.Remove(combatOrdinal);
    }

    internal static void ProcessEntry(object entry, IReadOnlyDictionary<string, string?> fields)
    {
        switch (entry)
        {
            case CardPlayStartedEntry:
                Push(fields);
                break;
            case CardPlayFinishedEntry:
                Pop();
                break;
            case DamageReceivedEntry:
                TryAttributeDamage(fields);
                break;
        }
    }

    private static void Push(IReadOnlyDictionary<string, string?> fields)
    {
        var ord = TelemetryScopeContext.Snapshot().CombatOrdinal;
        if (ord <= 0)
            return;
        var key = CardDamageParsing.TryCardDisplayKey(fields) ?? "?";
        lock (Gate)
        {
            if (!StackByCombat.TryGetValue(ord, out var list))
            {
                list = new List<string>(4);
                StackByCombat[ord] = list;
            }

            list.Add(key);
        }
    }

    private static void Pop()
    {
        var ord = TelemetryScopeContext.Snapshot().CombatOrdinal;
        if (ord <= 0)
            return;
        lock (Gate)
        {
            if (!StackByCombat.TryGetValue(ord, out var list) || list.Count == 0)
                return;
            list.RemoveAt(list.Count - 1);
        }
    }

    private static void TryAttributeDamage(IReadOnlyDictionary<string, string?> fields)
    {
        if (!CardDamageParsing.TryParseDamageAmount(fields, out var amt) || amt <= 0)
            return;

        var ord = TelemetryScopeContext.Snapshot().CombatOrdinal;
        if (ord <= 0)
            return;

        string? cardKey;
        lock (Gate)
        {
            if (!StackByCombat.TryGetValue(ord, out var list) || list.Count == 0)
            {
                cardKey = null;
            }
            else
            {
                cardKey = list[^1];
            }
        }

        if (cardKey is not null)
            TelemetryMetricsStore.ApplyAttributedCardDamage(cardKey, amt);
        else
            TelemetryMetricsStore.ApplyPassiveDamage(PassiveDamageLabel.Build(fields), amt);
    }
}

/// <summary>
/// Best-effort label for <see cref="DamageReceivedEntry"/> that occurs outside any card-play bracket
/// (poison ticks, thorns, power ticks, etc.).
/// </summary>
internal static class PassiveDamageLabel
{
    internal static string Build(IReadOnlyDictionary<string, string?> fields)
    {
        var sb = new StringBuilder(384);
        foreach (var kv in fields)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        }

        return ClassifyBlob(sb.ToString());
    }

    internal static string BuildFromJsonPayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return "unlabeled";
        var sb = new StringBuilder(384);
        foreach (var p in props.EnumerateObject())
        {
            sb.Append(p.Name).Append('=');
            if (p.Value.ValueKind == JsonValueKind.String)
                sb.Append(p.Value.GetString());
            else
                sb.Append(p.Value.ToString());
            sb.Append(';');
        }

        return ClassifyBlob(sb.ToString());
    }

    private static string ClassifyBlob(string blob)
    {
        if (string.IsNullOrEmpty(blob))
            return "unlabeled";
        var b = blob.ToLowerInvariant();
        if (b.Contains("poison") || b.Contains("noxious"))
            return "Poison";
        if (b.Contains("acid"))
            return "Acid";
        if (b.Contains("burn") || b.Contains("blaze"))
            return "Burn";
        if (b.Contains("thorns"))
            return "Thorns";
        if (b.Contains("retaliat"))
            return "Retaliation";
        if (b.Contains("pressure_points") || b.Contains("pressure points"))
            return "PressurePoints";
        if (b.Contains("corpse_explosion") || b.Contains("corpse explosion"))
            return "CorpseExplosion";
        if (b.Contains("explosive"))
            return "Explosive";
        if (b.Contains("evoke") || (b.Contains("lightning") && b.Contains("orb")))
            return "OrbEvoke";
        if (b.Contains("tick") && (b.Contains("power") || b.Contains("buff")))
            return "PowerTick";
        if (b.Contains("regen") && b.Contains("enemy"))
            return "EnemyRegenInverse";
        return "unlabeled";
    }
}

/// <summary>Shared parsing for live hooks and NDJSON replay.</summary>
internal static class CardDamageParsing
{
    internal static readonly string[] CardNamePropertyCandidates =
    [
        "CardName",
        "Name",
        "DisplayName",
        "LocalizedName",
        "BlueprintName",
        "CardBlueprintId",
        "CardId",
        "BlueprintId",
        "Card",
        "Id",
    ];

    internal static readonly string[] DamageAmountPropertyKeys =
    [
        "Amount", "Damage", "DamageAmount", "HitDamage", "FinalDamage", "Value", "DamageDealt",
        "OutgoingDamage", "TotalDamage", "DealtDamage", "RawDamage", "FinalDamageAmount",
        "HitPointsRemoved", "HealthLost", "HpLost", "Magnitude", "Loss", "TrueDamage",
    ];

    private static readonly string[] DamageNameHints =
    [
        "damage", "dmg", "hit", "harm", "attack", "dealt", "hurt",
    ];

    internal static string? TryCardDisplayKey(IReadOnlyDictionary<string, string?> fields)
    {
        foreach (var want in CardNamePropertyCandidates)
        {
            foreach (var kv in fields)
            {
                if (!string.Equals(kv.Key, want, StringComparison.OrdinalIgnoreCase))
                    continue;
                var t = kv.Value?.Trim();
                if (string.IsNullOrEmpty(t) || string.Equals(t, "<unreadable>", StringComparison.Ordinal))
                    continue;
                return t;
            }
        }

        return null;
    }

    internal static bool TryParseDamageAmount(IReadOnlyDictionary<string, string?> fields, out decimal value)
    {
        value = 0;
        foreach (var want in DamageAmountPropertyKeys)
        {
            foreach (var kv in fields)
            {
                if (!string.Equals(kv.Key, want, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                var s = kv.Value.Trim();
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                {
                    value = d;
                    return true;
                }

                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d))
                {
                    value = d;
                    return true;
                }
            }
        }

        decimal best = 0;
        var any = false;
        foreach (var kv in fields)
        {
            foreach (var hint in DamageNameHints)
            {
                if (kv.Key.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                var s = kv.Value.Trim();
                if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    && !decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d))
                    continue;
                if (d <= 0)
                    continue;
                if (!any || d > best)
                {
                    best = d;
                    any = true;
                }

                break;
            }
        }

        if (any)
        {
            value = best;
            return true;
        }

        return false;
    }
}
