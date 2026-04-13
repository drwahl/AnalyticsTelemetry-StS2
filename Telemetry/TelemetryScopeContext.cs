namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Thread-safe “where we are in the run” hints for routing metrics. Populated from run saves,
/// combat lifecycle hooks, and recent <see cref="MegaCrit.Sts2.Core.Commands.PlayerCmd"/> calls.
/// </summary>
internal static class TelemetryScopeContext
{
    private static readonly object Gate = new();

    private static string _runMode = "unknown";
    private static string? _accountKey;
    private static string? _profileFolder;
    private static int _actIndex = -1;
    private static string _actId = "";
    private static int _mapDepth;
    private static int _ascension;
    private static string[] _partyKeys = [];
    private static int _combatOrdinal;
    /// <summary>Per-combat hand counter (1-based), incremented when a player energy turn rollup is emitted.</summary>
    private static int _handSequence = 1;
    private static DateTime? _combatStartUtc;
    private static string? _lastEnergyPlayerKey;

    internal static void ResetForNewSession()
    {
        lock (Gate)
        {
            _runMode = "unknown";
            _accountKey = null;
            _profileFolder = null;
            _actIndex = -1;
            _actId = "";
            _mapDepth = 0;
            _ascension = 0;
            _partyKeys = [];
            _combatOrdinal = 0;
            _handSequence = 1;
            _combatStartUtc = null;
            _lastEnergyPlayerKey = null;
        }

        MapPathDecisionTelemetry.ResetForNewSession();
    }

    internal static void SetRunSaveMeta(string mode, string? accountKey, string? profileFolder)
    {
        lock (Gate)
        {
            _runMode = mode;
            _accountKey = accountKey;
            _profileFolder = profileFolder;
        }
    }

    internal static void SetRunMapContext(int actIndex, string actId, int mapDepth, int ascension, IReadOnlyList<string> partyPlayerKeys)
    {
        lock (Gate)
        {
            _actIndex = actIndex;
            _actId = actId;
            _mapDepth = mapDepth;
            _ascension = ascension;
            _partyKeys = partyPlayerKeys.Count == 0 ? [] : partyPlayerKeys.ToArray();
        }
    }

    internal static void OnCombatStarted()
    {
        lock (Gate)
        {
            _combatOrdinal++;
            _handSequence = 1;
            _combatStartUtc = DateTime.UtcNow;
        }
    }

    internal static void OnCombatEnded()
    {
        lock (Gate)
        {
            _combatStartUtc = null;
        }
    }

    /// <summary>1-based hand index for the current combat (energy-turn rollup id).</summary>
    internal static int CurrentHandSequence
    {
        get
        {
            lock (Gate)
                return _handSequence;
        }
    }

    internal static void AdvanceHandSequence()
    {
        lock (Gate)
            _handSequence++;
    }

    internal static void NoteEnergyPlayerKey(string playerKey)
    {
        lock (Gate)
            _lastEnergyPlayerKey = playerKey;
    }

    internal static TelemetryScopeSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new TelemetryScopeSnapshot(
                _runMode,
                _accountKey,
                _profileFolder,
                _actIndex,
                _actId,
                _mapDepth,
                _ascension,
                _partyKeys,
                _combatOrdinal,
                _handSequence,
                _combatStartUtc,
                _lastEnergyPlayerKey);
        }
    }
}

internal readonly record struct TelemetryScopeSnapshot(
    string RunMode,
    string? AccountKey,
    string? ProfileFolder,
    int ActIndex,
    string ActId,
    int MapDepth,
    int Ascension,
    string[] PartyPlayerKeys,
    int CombatOrdinal,
    int HandSequence,
    DateTime? CombatStartUtc,
    string? LastEnergyPlayerKey);
