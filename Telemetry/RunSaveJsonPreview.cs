using System.Text;
using System.Text.Json;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Lightweight parse of <c>current_run*.save</c> JSON for analytics context (not full game state).</summary>
internal static class RunSaveJsonPreview
{
    private const int MaxFileBytes = 14 * 1024 * 1024;

    internal static bool TryParse(string path, out RunSavePreview preview)
    {
        preview = default;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0)
                return false;
            var len = (int)Math.Min(fi.Length, MaxFileBytes);
            var buffer = new byte[len];
            using var fs = fi.OpenRead();
            var read = fs.Read(buffer, 0, len);
            if (read < 2)
                return false;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            using var doc = JsonDocument.Parse(text);
            return TryParseRoot(doc.RootElement, out preview);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRoot(JsonElement root, out RunSavePreview preview)
    {
        preview = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        var actIndex = -1;
        if (root.TryGetProperty("current_act_index", out var ai) && ai.TryGetInt32(out var aix))
            actIndex = aix;

        var asc = 0;
        if (root.TryGetProperty("ascension", out var ascEl) && ascEl.TryGetInt32(out var asv))
            asc = asv;

        var mapDepth = 0;
        if (root.TryGetProperty("visited_map_coords", out var vmc) && vmc.ValueKind == JsonValueKind.Array)
            mapDepth = vmc.GetArrayLength();

        var actId = "";
        if (root.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Array
            && actIndex >= 0 && actIndex < acts.GetArrayLength())
        {
            var act = acts[actIndex];
            if (act.ValueKind == JsonValueKind.Object
                && act.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String)
                actId = idEl.GetString() ?? "";
        }

        var partyKeys = new List<string>();
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in players.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                    continue;
                if (p.TryGetProperty("net_id", out var nid))
                {
                    if (nid.TryGetUInt64(out var u64))
                        partyKeys.Add(PlayerKeyUtil.FromNetId(u64));
                    else if (nid.ValueKind == JsonValueKind.String
                             && ulong.TryParse(nid.GetString(), out var u2))
                        partyKeys.Add(PlayerKeyUtil.FromNetId(u2));
                }
            }
        }

        var roomVisits = new Dictionary<string, int>(StringComparer.Ordinal);
        AccumulateMapPointRoomCounts(root, roomVisits);
        RunSaveGoldParser.TryReadGoldFromRunSaveRoot(root, out var gold);

        preview = new RunSavePreview(actIndex, actId, mapDepth, asc, partyKeys, roomVisits, gold);
        return true;
    }

    /// <summary>
    /// Walk <c>map_point_history</c> (acts → floors) and count each visited point by
    /// <c>map_point_type</c> or, if missing, <c>rooms[0].room_type</c> (same idea as spirescope).
    /// </summary>
    private static void AccumulateMapPointRoomCounts(JsonElement root, Dictionary<string, int> counts)
    {
        if (!root.TryGetProperty("map_point_history", out var mph) || mph.ValueKind != JsonValueKind.Array)
            return;

        foreach (var act in mph.EnumerateArray())
        {
            if (act.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var floor in act.EnumerateArray())
            {
                if (floor.ValueKind != JsonValueKind.Object)
                    continue;
                var type = "";
                if (floor.TryGetProperty("map_point_type", out var mpt) && mpt.ValueKind == JsonValueKind.String)
                    type = (mpt.GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(type)
                    && floor.TryGetProperty("rooms", out var rooms)
                    && rooms.ValueKind == JsonValueKind.Array
                    && rooms.GetArrayLength() > 0)
                {
                    var room0 = rooms[0];
                    if (room0.ValueKind == JsonValueKind.Object
                        && room0.TryGetProperty("room_type", out var rt)
                        && rt.ValueKind == JsonValueKind.String)
                        type = (rt.GetString() ?? "").Trim();
                }

                if (string.IsNullOrEmpty(type))
                    type = "unknown";
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }
    }
}

internal readonly record struct RunSavePreview(
    int ActIndex,
    string ActId,
    int MapDepth,
    int Ascension,
    IReadOnlyList<string> PartyPlayerKeys,
    IReadOnlyDictionary<string, int> RoomVisitsByType,
    int? Gold);
