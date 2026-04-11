using System.Security.Cryptography;
using AnalyticsTelemetry.AnalyticsTelemetryCode;
using System.Text;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Observes run save files under <c>user://steam/&lt;account&gt;/profile*/saves/current_run*.save</c>
/// without patching game code. Events are coarse (appear / progress / removed) and safe across versions.
/// </summary>
public static class RunSaveTelemetry
{
    private const int PollMs = 2500;
    private const int ProgressMinIntervalSec = 45;

    private static readonly LockState Gate = new();
    private static Thread? _thread;
    private static volatile bool _stop;

    private sealed class FileSnap
    {
        public long Length;
        public DateTime LastWriteUtc;
        public DateTime LastProgressEmitUtc;
        public string? LastContextFingerprint;
        public int? LastEmittedGold;
    }

    private sealed class LockState
    {
        public readonly Dictionary<string, FileSnap> Snaps = new();
        public HashSet<string> LastPaths = new();
    }

    public static void Start()
    {
        if (_thread is { IsAlive: true })
            return;

        _stop = false;
        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "AnalyticsTelemetry-RunSaves",
        };
        _thread.Start();
    }

    public static void Stop()
    {
        _stop = true;
        _thread?.Join(millisecondsTimeout: 2000);
    }

    private static void PollLoop()
    {
        while (!_stop)
        {
            try
            {
                ScanOnce();
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"RunSaveTelemetry scan error: {e}");
            }

            Thread.Sleep(PollMs);
        }
    }

    private static void ScanOnce()
    {
        var userData = Godot.OS.GetUserDataDir();
        var steamRoot = Path.Combine(userData, "steam");
        if (!Directory.Exists(steamRoot))
            return;

        var now = DateTime.UtcNow;
        var currentPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var accountDir in Directory.GetDirectories(steamRoot))
        {
            var accountKey = ShortHash(Path.GetFileName(accountDir));

            foreach (var profileDir in Directory.GetDirectories(accountDir)
                         .Where(d => Path.GetFileName(d).StartsWith("profile", StringComparison.Ordinal)))
            {
                var profileName = Path.GetFileName(profileDir);
                var savesDir = Path.Combine(profileDir, "saves");
                if (!Directory.Exists(savesDir))
                    continue;

                TouchSave(savesDir, "current_run.save", "solo", accountKey, profileName, currentPaths, now);
                TouchSave(savesDir, "current_run_mp.save", "mp", accountKey, profileName, currentPaths, now);
            }
        }

        lock (Gate)
        {
            foreach (var prev in Gate.LastPaths)
            {
                if (currentPaths.Contains(prev))
                    continue;

                Gate.Snaps.Remove(prev);
                var parts = ClassifyPath(prev);
                if (parts is not null)
                {
                    TelemetryEventLog.WriteRaw(
                        "run_save_removed",
                        new RunSaveRemovedPayload(
                            parts.Value.AccountKey,
                            parts.Value.Profile,
                            parts.Value.Mode),
                        DateTime.UtcNow);
                }
            }

            Gate.LastPaths = currentPaths;
        }
    }

    private static void TouchSave(
        string savesDir,
        string fileName,
        string mode,
        string accountKey,
        string profileName,
        HashSet<string> currentPaths,
        DateTime nowUtc)
    {
        var path = Path.Combine(savesDir, fileName);
        if (!File.Exists(path))
            return;

        currentPaths.Add(path);

        var info = new FileInfo(path);
        var len = info.Length;
        var mtime = info.LastWriteTimeUtc;

        lock (Gate)
        {
            if (!Gate.Snaps.TryGetValue(path, out var snap))
            {
                Gate.Snaps[path] = new FileSnap
                {
                    Length = len,
                    LastWriteUtc = mtime,
                    LastProgressEmitUtc = nowUtc,
                };

                TelemetryEventLog.WriteRaw(
                    "run_save_appeared",
                    new RunSaveSnapshotPayload(
                        accountKey,
                        profileName,
                        mode,
                        len,
                        mtime),
                    mtime);
                MaybeEmitRunContextSnapshot(path, accountKey, profileName, mode, Gate.Snaps[path], forceEmit: true);
                MaybeEmitRunGold(path, accountKey, profileName, mode, mtime, Gate.Snaps[path]);
                return;
            }

            var changed = len != snap.Length || mtime != snap.LastWriteUtc;
            if (!changed)
                return;

            snap.Length = len;
            snap.LastWriteUtc = mtime;
            MaybeEmitRunGold(path, accountKey, profileName, mode, mtime, snap);

            if ((nowUtc - snap.LastProgressEmitUtc).TotalSeconds >= ProgressMinIntervalSec)
            {
                snap.LastProgressEmitUtc = nowUtc;
                TelemetryEventLog.WriteRaw(
                    "run_save_progress",
                    new RunSaveSnapshotPayload(
                        accountKey,
                        profileName,
                        mode,
                        len,
                        mtime),
                    mtime);
                MaybeEmitRunContextSnapshot(path, accountKey, profileName, mode, snap, forceEmit: false);
            }
        }
    }

    private static (string AccountKey, string Profile, string Mode)? ClassifyPath(string path)
    {
        var name = Path.GetFileName(path);
        var mode = name switch
        {
            "current_run.save" => "solo",
            "current_run_mp.save" => "mp",
            _ => "unknown",
        };

        var savesDir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(savesDir))
            return null;
        var profileDir = Path.GetDirectoryName(savesDir);
        if (string.IsNullOrEmpty(profileDir))
            return null;
        var profile = Path.GetFileName(profileDir);
        var accountDir = Path.GetDirectoryName(profileDir);
        if (string.IsNullOrEmpty(accountDir))
            return null;
        var account = Path.GetFileName(accountDir);

        return (ShortHash(account), profile, mode);
    }

    private static string BuildRunContextFingerprint(RunSavePreview p)
    {
        var roomFp = string.Join(
            ',',
            p.RoomVisitsByType.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var goldPart = p.Gold?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "_";
        return
            $"{p.ActIndex}|{p.MapDepth}|{p.Ascension}|{p.PartyPlayerKeys.Count}|{string.Join(',', p.PartyPlayerKeys)}|{p.ActId}|{roomFp}|g{goldPart}";
    }

    private static void MaybeEmitRunContextSnapshot(
        string path,
        string accountKey,
        string profileName,
        string mode,
        FileSnap snap,
        bool forceEmit)
    {
        if (!RunSaveJsonPreview.TryParse(path, out var preview))
            return;

        TelemetryScopeContext.SetRunSaveMeta(mode, accountKey, profileName);
        TelemetryScopeContext.SetRunMapContext(
            preview.ActIndex,
            preview.ActId,
            preview.MapDepth,
            preview.Ascension,
            preview.PartyPlayerKeys);

        var fp = BuildRunContextFingerprint(preview);
        if (!forceEmit && fp == snap.LastContextFingerprint)
            return;

        snap.LastContextFingerprint = fp;
        TelemetryEventLog.WriteRaw(
            "run_context_snapshot",
            new RunContextSnapshotPayload(
                accountKey,
                profileName,
                mode,
                preview.ActIndex,
                preview.ActId,
                preview.MapDepth,
                preview.Ascension,
                preview.PartyPlayerKeys.Count,
                preview.PartyPlayerKeys.ToArray(),
                preview.RoomVisitsByType.Count > 0 ? preview.RoomVisitsByType : null,
                preview.Gold),
            DateTime.UtcNow);
    }

    private static void MaybeEmitRunGold(
        string path,
        string accountKey,
        string profileName,
        string mode,
        DateTime occurredUtc,
        FileSnap snap)
    {
        if (!RunSaveJsonPreview.TryParse(path, out var preview) || preview.Gold is not { } g)
            return;
        if (snap.LastEmittedGold == g)
            return;
        var prev = snap.LastEmittedGold;
        snap.LastEmittedGold = g;
        TelemetryEventLog.WriteRaw(
            "run_gold",
            new RunGoldPayload(accountKey, profileName, mode, g, prev),
            occurredUtc);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }
}

public sealed record RunSaveSnapshotPayload(
    string AccountKey,
    string ProfileFolder,
    string Mode,
    long LengthBytes,
    DateTime LastWriteUtc);

public sealed record RunSaveRemovedPayload(
    string AccountKey,
    string ProfileFolder,
    string Mode);
