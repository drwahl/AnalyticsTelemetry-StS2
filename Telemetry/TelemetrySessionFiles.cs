using AnalyticsTelemetry.AnalyticsTelemetryCode;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Lists NDJSON session files for the mod config “Session files” tab.</summary>
internal static class TelemetrySessionFiles
{
    internal static string SessionsDirectory() =>
        Path.Combine(Godot.OS.GetUserDataDir(), MainFile.ModId, "sessions");

    internal static List<SessionFileEntry> ListRecent(int max = 24)
    {
        var dir = SessionsDirectory();
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "*.ndjson")
            .Select(f => new SessionFileEntry(Path.GetFileName(f), f, File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(x => x.LastWriteUtc)
            .Take(max)
            .ToList();
    }

    /// <summary>Last <paramref name="maxLines"/> lines, capped by character count (for huge files).</summary>
    internal static string ReadTail(string path, int maxLines = 80, int maxChars = 24_000)
    {
        if (!File.Exists(path))
            return "(file not found)";

        try
        {
            var buffer = new Queue<string>(maxLines + 1);
            foreach (var line in File.ReadLines(path))
            {
                buffer.Enqueue(line);
                while (buffer.Count > maxLines)
                    buffer.Dequeue();
            }

            var text = string.Join("\n", buffer);
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n… (truncated)";
            return string.IsNullOrEmpty(text) ? "(empty)" : text;
        }
        catch (Exception e)
        {
            return $"(read error: {e.Message})";
        }
    }
}

internal readonly record struct SessionFileEntry(string FileName, string FullPath, DateTime LastWriteUtc);
