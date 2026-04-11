using System.IO;
using System.Text;
using System.Text.Json;
using AnalyticsTelemetry.AnalyticsTelemetryCode;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>What to aggregate for metrics: live memory vs replayed NDJSON on disk.</summary>
internal enum TelemetryDatasetKind
{
    /// <summary>In-process <see cref="TelemetryMetricsStore"/> only.</summary>
    CurrentSession = 0,

    /// <summary>Every <c>*.ndjson</c> under sessions/ (replay).</summary>
    AllSavedSessions = 1,

    Last24Hours = 2,
    Last7Days = 3,
    Last30Days = 4,
    Last365Days = 5,

    /// <summary>One file; <see cref="TelemetryDatasetSelection.SingleFileFullPath"/> set.</summary>
    SingleSessionFile = 6,
}

internal readonly record struct TelemetryDatasetSelection(TelemetryDatasetKind Kind, string? SingleFileFullPath)
{
    internal static TelemetryDatasetSelection Current => new(TelemetryDatasetKind.CurrentSession, null);
}

/// <summary>Lists session files and resolves which paths match a <see cref="TelemetryDatasetSelection"/>.</summary>
internal static class TelemetryDatasetCatalog
{
    internal sealed record SessionDescriptor(string FullPath, string FileName, DateTime LastWriteUtc, DateTime? StartEnvelopeUtc);

    internal static string SessionsDirectory() =>
        Path.Combine(Godot.OS.GetUserDataDir(), MainFile.ModId, "sessions");

    /// <summary>Newest first (by last write).</summary>
    internal static List<SessionDescriptor> ListSessions(int max = 64)
    {
        var dir = SessionsDirectory();
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "*.ndjson")
            .Select(path =>
            {
                var lw = File.GetLastWriteTimeUtc(path);
                DateTime? start = TryPeekFirstEnvelopeUtc(path, out var su) ? su : null;
                return new SessionDescriptor(path, Path.GetFileName(path), lw, start);
            })
            .OrderByDescending(x => x.LastWriteUtc)
            .Take(max)
            .ToList();
    }

    internal static bool TryPeekFirstEnvelopeUtc(string path, out DateTime utc)
    {
        utc = default;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var line = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return false;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("utc", out var u))
                return false;
            return TryReadUtc(u, out utc);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadUtc(JsonElement u, out DateTime utc)
    {
        utc = default;
        try
        {
            if (u.ValueKind == JsonValueKind.String)
            {
                var s = u.GetString();
                if (string.IsNullOrEmpty(s))
                    return false;
                utc = DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (utc.Kind == DateTimeKind.Unspecified)
                    utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                return true;
            }

            if (u.ValueKind == JsonValueKind.Number && u.TryGetInt64(out var ms))
            {
                utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <exception cref="InvalidOperationException">If <paramref name="sel"/> is <see cref="TelemetryDatasetKind.CurrentSession"/>.</exception>
    internal static IReadOnlyList<string> ResolvePaths(TelemetryDatasetSelection sel, out string summary)
    {
        summary = "";
        if (sel.Kind == TelemetryDatasetKind.CurrentSession)
            throw new InvalidOperationException("Use live store for current session.");

        switch (sel.Kind)
        {
            case TelemetryDatasetKind.AllSavedSessions:
                var all = ListSessions(512);
                summary = all.Count == 0
                    ? "No session files on disk yet."
                    : $"All saved sessions — {all.Count} file(s), replayed from disk.";
                return all.Select(x => x.FullPath).ToList();

            case TelemetryDatasetKind.Last24Hours:
                return FilterByAge(TimeSpan.FromHours(24), out summary, "Last 24 hours");
            case TelemetryDatasetKind.Last7Days:
                return FilterByAge(TimeSpan.FromDays(7), out summary, "Last 7 days");
            case TelemetryDatasetKind.Last30Days:
                return FilterByAge(TimeSpan.FromDays(30), out summary, "Last 30 days");
            case TelemetryDatasetKind.Last365Days:
                return FilterByAge(TimeSpan.FromDays(365), out summary, "Last 365 days");

            case TelemetryDatasetKind.SingleSessionFile:
                if (string.IsNullOrEmpty(sel.SingleFileFullPath) || !File.Exists(sel.SingleFileFullPath))
                {
                    summary = "Selected file is missing.";
                    return Array.Empty<string>();
                }

                summary = $"Single file — {Path.GetFileName(sel.SingleFileFullPath)}";
                return new[] { sel.SingleFileFullPath };

            default:
                summary = "";
                return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> FilterByAge(TimeSpan window, out string summary, string label)
    {
        var cut = DateTime.UtcNow - window;
        var list = ListSessions(512)
            .Where(d => (d.StartEnvelopeUtc ?? d.LastWriteUtc) >= cut)
            .ToList();
        summary = list.Count == 0
            ? $"{label} — no session files in range (by first-line time or file mtime)."
            : $"{label} — {list.Count} session file(s) in range (replay).";
        return list.Select(x => x.FullPath).ToList();
    }

    /// <summary>Cheap invalidation key for aggregate cache (paths + coarse size).</summary>
    /// <summary>Maps Settings → Mods dataset <see cref="Godot.OptionButton"/> index ↔ <see cref="TelemetryDatasetSelection"/>.</summary>
    internal static int DropdownIndexFromSelection(
        TelemetryDatasetSelection sel,
        IReadOnlyList<SessionDescriptor> filesOrderedNewestFirst)
    {
        switch (sel.Kind)
        {
            case TelemetryDatasetKind.CurrentSession:
                return 0;
            case TelemetryDatasetKind.AllSavedSessions:
                return 1;
            case TelemetryDatasetKind.Last24Hours:
                return 2;
            case TelemetryDatasetKind.Last7Days:
                return 3;
            case TelemetryDatasetKind.Last30Days:
                return 4;
            case TelemetryDatasetKind.Last365Days:
                return 5;
            case TelemetryDatasetKind.SingleSessionFile:
                for (var i = 0; i < filesOrderedNewestFirst.Count; i++)
                {
                    if (string.Equals(
                            filesOrderedNewestFirst[i].FullPath,
                            sel.SingleFileFullPath,
                            StringComparison.Ordinal))
                        return 6 + i;
                }

                return 0;
            default:
                return 0;
        }
    }

    internal static TelemetryDatasetSelection SelectionFromDropdownIndex(
        int index,
        IReadOnlyList<SessionDescriptor> filesOrderedNewestFirst)
    {
        switch (index)
        {
            case 0:
                return TelemetryDatasetSelection.Current;
            case 1:
                return new TelemetryDatasetSelection(TelemetryDatasetKind.AllSavedSessions, null);
            case 2:
                return new TelemetryDatasetSelection(TelemetryDatasetKind.Last24Hours, null);
            case 3:
                return new TelemetryDatasetSelection(TelemetryDatasetKind.Last7Days, null);
            case 4:
                return new TelemetryDatasetSelection(TelemetryDatasetKind.Last30Days, null);
            case 5:
                return new TelemetryDatasetSelection(TelemetryDatasetKind.Last365Days, null);
            default:
            {
                var fi = index - 6;
                if (fi >= 0 && fi < filesOrderedNewestFirst.Count)
                    return new TelemetryDatasetSelection(
                        TelemetryDatasetKind.SingleSessionFile,
                        filesOrderedNewestFirst[fi].FullPath);
                return TelemetryDatasetSelection.Current;
            }
        }
    }

    internal static string FingerprintReplaySources(IReadOnlyList<string> paths)
    {
        var sb = new StringBuilder(256);
        foreach (var p in paths.OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append(p);
            sb.Append('|');
            if (!File.Exists(p))
            {
                sb.Append('?');
                continue;
            }

            try
            {
                var len = new FileInfo(p).Length;
                sb.Append((len / 8192).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                sb.Append('?');
            }
        }

        return sb.ToString();
    }
}
