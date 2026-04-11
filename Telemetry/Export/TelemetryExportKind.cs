namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>Which remote writer to construct from <see cref="TelemetryExportPreferences"/>.</summary>
internal enum TelemetryExportKind
{
    None = 0,
    /// <summary>HTTP POST with InfluxDB line protocol body (works with VictoriaMetrics <c>/write</c>, Influx 1.x/2.x write APIs).</summary>
    InfluxLineProtocolHttp = 1,
}
