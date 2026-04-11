using System.Globalization;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat.History.Entries;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Who received a power / affliction line in combat history (best-effort string heuristics).</summary>
internal enum StatusEffectRecipientKind
{
    Unknown = 0,
    ToPlayer = 1,
    ToEnemy = 2,
}

/// <summary>
/// Parses <c>combat_history_power_received</c> and <c>combat_history_card_afflicted</c> for rollups + NDJSON hints.
/// </summary>
internal static class CombatHistoryStatusEffectMetrics
{
    private static readonly string[] EffectKeyPropertyCandidates =
    [
        "PowerName", "Power", "PowerId", "PowerBlueprintName", "PowerBlueprintId",
        "DisplayName", "LocalizedName", "Name",
        "Affliction", "Afflictions", "AfflictionName", "AfflictionType",
        "Status", "EffectName", "BuffName", "DebuffName", "ModifierName",
        "Key", "BlueprintId", "Type", "Id",
    ];

    /// <summary>Live NDJSON line: derive recipient + effect key from flattened <see cref="CombatHistoryEntryPayload"/> properties.</summary>
    internal static bool TryDeriveFromDictionary(
        IReadOnlyDictionary<string, string?> fields,
        string entryTypeName,
        out StatusEffectDerivation? derivation)
    {
        derivation = null;
        var lineKind = LineKindFromEntryType(entryTypeName);
        if (lineKind is null)
            return false;

        var blob = BuildBlobFromDictionary(fields);
        TryExtractEffectKeyFromDictionary(fields, out var effectKey);
        var recipient = ClassifyRecipient(blob, cardAfflicted: lineKind == "afflict");
        derivation = new StatusEffectDerivation(lineKind, RecipientToken(recipient), NormalizeEffectKey(effectKey));
        return true;
    }

    internal static bool TryDeriveFromJson(
        JsonElement payload,
        string eventType,
        out StatusEffectRecipientKind recipient,
        out string effectKey,
        out string lineKind)
    {
        recipient = StatusEffectRecipientKind.Unknown;
        effectKey = "unlabeled";
        lineKind = eventType switch
        {
            "combat_history_power_received" => "power",
            "combat_history_card_afflicted" => "afflict",
            _ => "",
        };
        if (lineKind.Length == 0)
            return false;

        TryBuildBlobFromJson(payload, out var blob);
        TryExtractEffectKeyFromJson(payload, out effectKey);
        effectKey = NormalizeEffectKey(effectKey);
        recipient = ClassifyRecipient(blob, cardAfflicted: lineKind == "afflict");
        return true;
    }

    private static string? LineKindFromEntryType(string entryTypeName) =>
        entryTypeName switch
        {
            nameof(PowerReceivedEntry) => "power",
            nameof(CardAfflictedEntry) => "afflict",
            _ => null,
        };

    private static string RecipientToken(StatusEffectRecipientKind k) =>
        k switch
        {
            StatusEffectRecipientKind.ToPlayer => "to_player",
            StatusEffectRecipientKind.ToEnemy => "to_enemy",
            _ => "unknown",
        };

    private static string NormalizeEffectKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unlabeled";
        var s = raw.Trim();
        if (s.Length > 72)
            s = s[..72];
        return s;
    }

    private static string BuildBlobFromDictionary(IReadOnlyDictionary<string, string?> fields)
    {
        var sb = new StringBuilder(384);
        foreach (var kv in fields)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        }

        return sb.ToString();
    }

    private static bool TryBuildBlobFromJson(JsonElement payload, out string blob)
    {
        blob = "";
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return false;
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

        blob = sb.ToString();
        return blob.Length > 0;
    }

    private static void TryExtractEffectKeyFromDictionary(IReadOnlyDictionary<string, string?> fields, out string effectKey)
    {
        foreach (var name in EffectKeyPropertyCandidates)
        {
            if (!fields.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v))
                continue;
            if (string.Equals(v, "<unreadable>", StringComparison.Ordinal))
                continue;
            effectKey = v.Trim();
            return;
        }

        foreach (var kv in fields)
        {
            if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value))
                continue;
            var kn = kv.Key;
            if (kn.Contains("Afflict", StringComparison.OrdinalIgnoreCase)
                || kn.Contains("Power", StringComparison.OrdinalIgnoreCase)
                || kn.Contains("Debuff", StringComparison.OrdinalIgnoreCase)
                || kn.Contains("Status", StringComparison.OrdinalIgnoreCase))
            {
                effectKey = kv.Value.Trim();
                return;
            }
        }

        effectKey = "unlabeled";
    }

    private static void TryExtractEffectKeyFromJson(JsonElement payload, out string effectKey)
    {
        effectKey = "unlabeled";
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return;

        foreach (var name in EffectKeyPropertyCandidates)
        {
            if (!props.TryGetProperty(name, out var el))
                continue;
            var s = JsonScalarToString(el);
            if (!string.IsNullOrWhiteSpace(s))
            {
                effectKey = s;
                return;
            }
        }

        foreach (var p in props.EnumerateObject())
        {
            if (p.Name.Contains("Afflict", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Debuff", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase))
            {
                var s = JsonScalarToString(p.Value);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    effectKey = s;
                    return;
                }
            }
        }
    }

    private static string? JsonScalarToString(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d.ToString(CultureInfo.InvariantCulture) : el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool BlobSuggestsPlayerCardContext(string blob)
    {
        ReadOnlySpan<string> hints =
        [
            "Hand", "Deck", "DrawPile", "DiscardPile", "ExhaustPile", "CardPile", "PileOwner",
            "OwnerPlayer", "PlayerHand", "PlayerDeck",
        ];
        foreach (var h in hints)
        {
            if (blob.Contains(h, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static StatusEffectRecipientKind ClassifyRecipient(string blob, bool cardAfflicted)
    {
        var pl = TelemetryMetricsStore.BlobSuggestsPlayerVictim(blob)
                 || (cardAfflicted && BlobSuggestsPlayerCardContext(blob));
        var en = TelemetryMetricsStore.BlobSuggestsEnemyVictim(blob);
        if (pl && !en)
            return StatusEffectRecipientKind.ToPlayer;
        if (en && !pl)
            return StatusEffectRecipientKind.ToEnemy;
        if (cardAfflicted && !en && !pl)
            return StatusEffectRecipientKind.ToPlayer;
        return StatusEffectRecipientKind.Unknown;
    }
}
