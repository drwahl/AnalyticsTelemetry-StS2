using System.Text.Json;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Best-effort gold read from <c>current_run*.save</c> JSON (field names vary by build).</summary>
internal static class RunSaveGoldParser
{
    private static readonly string[] RootKeys =
    [
        "gold", "player_gold", "playerGold", "currency", "coins", "money", "display_gold", "displayGold",
        "runGold", "run_gold",
    ];

    private static readonly string[] PlayerKeys =
    [
        "gold", "player_gold", "playerGold", "currency", "coins", "money",
    ];

    internal static bool TryReadGoldFromRunSaveRoot(JsonElement root, out int? gold)
    {
        gold = null;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (TryReadIntFromObject(root, RootKeys, out var v))
        {
            gold = v;
            return true;
        }

        if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in players.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object)
                continue;
            if (TryReadIntFromObject(p, PlayerKeys, out v))
            {
                gold = v;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadIntFromObject(JsonElement obj, string[] names, out int value)
    {
        value = 0;
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
                return true;
            if (el.ValueKind == JsonValueKind.String
                && int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }
}
