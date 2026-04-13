using System.Globalization;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Best-effort player attribution for <see cref="MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayStartedEntry"/> reflection fields (MP).</summary>
internal static class CardPlayOrderPlayerParsing
{
    internal static string? TryPlayerKey(IReadOnlyDictionary<string, string?> fields)
    {
        if (fields.TryGetValue("playerKey", out var pk) && !string.IsNullOrWhiteSpace(pk))
            return pk.Trim();

        foreach (var key in new[] { "NetId", "PlayerNetId", "OwnerNetId", "PlayerId", "Owner" })
        {
            if (!fields.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
                continue;
            var t = s.Trim();
            if (ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64))
                return PlayerKeyUtil.FromNetId(u64);
            if (ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u64))
                return PlayerKeyUtil.FromNetId(u64);
        }

        return null;
    }
}
