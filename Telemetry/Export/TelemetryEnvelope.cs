using System.Text.Json;

namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>One logical telemetry row (same envelope as NDJSON, before formatting per sink).</summary>
internal readonly struct TelemetryEnvelope
{
    public int SchemaVersion { get; init; }
    public string EventType { get; init; }
    public long Seq { get; init; }
    public DateTime OccurredUtc { get; init; }
    public JsonElement Payload { get; init; }
}
