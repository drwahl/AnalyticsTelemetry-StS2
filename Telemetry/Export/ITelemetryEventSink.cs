namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>
/// Receives each logged event. Implementations must be thread-safe if <see cref="Write"/> may be called
/// under a lock; prefer O(1) enqueue and flush on a background thread for network sinks.
/// </summary>
internal interface ITelemetryEventSink : IDisposable
{
    /// <summary>Short label for logs (e.g. <c>ndjson</c>, <c>influx_line_http</c>).</summary>
    string SinkId { get; }

    /// <param name="ndjsonLine">Full JSON line written to disk (NDJSON sink uses this verbatim).</param>
    void Write(in TelemetryEnvelope envelope, string ndjsonLine);
}
