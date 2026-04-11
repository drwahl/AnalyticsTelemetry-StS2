using System.Reflection;
using System.Text;
using System.Text.Json;
using AnalyticsTelemetry.Telemetry.Export;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// Append-only NDJSON log under the game's user data directory:
/// &lt;UserData&gt;/AnalyticsTelemetry/sessions/*.ndjson
/// </summary>
/// <remarks>
/// <para><b>Timing:</b> Each line's <c>utc</c> is the wall-clock instant the <i>thing</i> happened (play, click, etc.),
/// captured at observation time. Writes may be delayed or batched later; callers that care should pass
/// <see cref="WriteRaw(string, object, DateTime)"/> with <paramref name="occurredUtc"/> from that moment, not from flush.</para>
/// <para>Callers that omit <paramref name="occurredUtc"/> use "now" at write time (fine for coarse pollers).</para>
/// </remarks>
public static class TelemetryEventLog
{
    private const int MaxUiLines = 48;
    private const int MaxUiCharsPerLine = 220;

    private static readonly object Gate = new();
    private static readonly object UiGate = new();
    private static readonly List<string> UiLines = new();
    private static readonly List<ITelemetryEventSink> _sinks = new();
    private static NdjsonFileTelemetrySink? _ndjsonSink;
    private static long _seq;
    private static string? _sessionPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static void Init(string modId)
    {
        lock (Gate)
        {
            TelemetryExportPreferences.LoadFromDisk();
            DisposeRemoteSinksLocked();
            _ndjsonSink = new NdjsonFileTelemetrySink(modId, out var path);
            _sessionPath = path;
            _sinks.Clear();
            _sinks.Add(_ndjsonSink);
            var remote = TelemetrySinkFactory.TryCreateRemoteSink();
            if (remote is not null)
                _sinks.Add(remote);

            TelemetryScopeContext.ResetForNewSession();
            TelemetryMetricsStore.Reset();
            var hostRefs = TelemetryDiagnostics.CaptureHostReferences();
            Append(
                "session_start",
                new SessionStartPayload(modId, ModAssemblyVersionString(), EngineVersionString(), _sessionPath, hostRefs),
                DateTime.UtcNow);
        }
    }

    /// <summary>Re-read <see cref="TelemetryExportPreferences"/> and replace remote sink(s) without losing the NDJSON file.</summary>
    public static void ReloadRemoteSinksFromPreferences()
    {
        TelemetryExportPreferences.LoadFromDisk();
        lock (Gate)
        {
            DisposeRemoteSinksLocked();
            var remote = TelemetrySinkFactory.TryCreateRemoteSink();
            if (remote is not null)
                _sinks.Add(remote);
        }
    }

    private static void DisposeRemoteSinksLocked()
    {
        for (var i = _sinks.Count - 1; i >= 1; i--)
        {
            _sinks[i].Dispose();
            _sinks.RemoveAt(i);
        }
    }

    private static string EngineVersionString()
    {
        var v = Godot.Engine.GetVersionInfo();
        return v.ToString();
    }

    private static string ModAssemblyVersionString()
    {
        var asm = typeof(TelemetryEventLog).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info;
        return asm.GetName().Version?.ToString() ?? "?";
    }

    private static void Append(string eventType, object payload, DateTime occurredUtc)
    {
        if (_ndjsonSink is null)
            return;

        var seq = Interlocked.Increment(ref _seq);
        var payloadElement = JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);
        TelemetryMetricsStore.RecordEvent(eventType, payloadElement);
        var doc = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["eventType"] = eventType,
            ["seq"] = seq,
            ["utc"] = occurredUtc,
            ["payload"] = payloadElement,
        };
        var line = JsonSerializer.Serialize(doc, JsonOptions);
        var envelope = new TelemetryEnvelope
        {
            SchemaVersion = 1,
            EventType = eventType,
            Seq = seq,
            OccurredUtc = occurredUtc,
            Payload = payloadElement,
        };
        foreach (var sink in _sinks)
            sink.Write(in envelope, line);
        EnqueueUiLine(line);
    }

    private static void EnqueueUiLine(string line)
    {
        if (line.Length > MaxUiCharsPerLine)
            line = line[..(MaxUiCharsPerLine - 3)] + "...";

        lock (UiGate)
        {
            UiLines.Add(line);
            while (UiLines.Count > MaxUiLines)
                UiLines.RemoveAt(0);
        }
    }

    /// <summary>Snapshot for the on-screen debug panel (newest lines last).</summary>
    public static string[] GetRecentLinesSnapshot()
    {
        lock (UiGate)
            return UiLines.ToArray();
    }

    /// <summary>Clears the rolling NDJSON preview buffer in the overlay / mod UI. Does not affect disk or event sequence numbers.</summary>
    public static void ClearRecentUiLines()
    {
        lock (UiGate)
            UiLines.Clear();
    }

    /// <summary>
    /// Human-readable log lines for the overlay (vs raw JSON). Newest lines last, same order as <see cref="GetRecentLinesSnapshot"/>.
    /// </summary>
    public static string[] GetRecentLinesForDisplay(bool rawNdjson)
    {
        var lines = GetRecentLinesSnapshot();
        if (rawNdjson)
            return lines;
        var copy = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            copy[i] = SummarizeNdjsonLine(lines[i]);
        return copy;
    }

    private static string SummarizeNdjsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("eventType", out var et) || et.ValueKind != JsonValueKind.String)
                return Truncate(line, 100);
            var type = et.GetString() ?? "?";
            root.TryGetProperty("seq", out var seqEl);
            var seqStr = seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt64(out var sq)
                ? sq.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "—";
            var sb = new StringBuilder(120);
            sb.Append('[').Append(seqStr).Append("] ").Append(type);
            if (root.TryGetProperty("payload", out var pay) && pay.ValueKind == JsonValueKind.Object)
            {
                if (pay.TryGetProperty("modId", out var mid) && mid.ValueKind == JsonValueKind.String)
                    sb.Append(" · ").Append(mid.GetString());
                if (string.Equals(type, "run_gold", StringComparison.Ordinal)
                    && pay.TryGetProperty("gold", out var gEl)
                    && gEl.ValueKind == JsonValueKind.Number
                    && gEl.TryGetInt32(out var goldV))
                    sb.Append(" · ").Append(goldV.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("g");
            }

            return sb.ToString();
        }
        catch
        {
            return Truncate(line, 100);
        }
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }

    /// <summary>Log an event; envelope <c>utc</c> is <see cref="DateTime.UtcNow"/> at write time.</summary>
    public static void WriteRaw(string eventType, object payload)
    {
        WriteRaw(eventType, payload, DateTime.UtcNow);
    }

    /// <summary>
    /// Log an event; envelope <c>utc</c> is <paramref name="occurredUtc"/> (capture at hook / observation, even if you flush later).
    /// </summary>
    public static void WriteRaw(string eventType, object payload, DateTime occurredUtc)
    {
        lock (Gate)
        {
            Append(eventType, payload, occurredUtc);
        }
    }

    public static string? SessionPath
    {
        get
        {
            lock (Gate)
                return _sessionPath;
        }
    }
}

public sealed record SessionStartPayload(
    string ModId,
    string ModVersion,
    string EngineVersion,
    string LogPath,
    HostReferenceSnapshot? HostReferences);
