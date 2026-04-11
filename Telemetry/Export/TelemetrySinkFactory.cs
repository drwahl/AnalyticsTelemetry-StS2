namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>Constructs optional remote <see cref="ITelemetryEventSink"/> instances from preferences.</summary>
/// <remarks>Add new backends by extending <see cref="TelemetryExportKind"/> and branching here.</remarks>
internal static class TelemetrySinkFactory
{
    public static ITelemetryEventSink? TryCreateRemoteSink()
    {
        if (!TelemetryExportPreferences.RemoteEnabled)
            return null;

        return TelemetryExportPreferences.Kind switch
        {
            TelemetryExportKind.InfluxLineProtocolHttp => InfluxLineProtocolHttpTelemetrySink.TryCreate(),
            TelemetryExportKind.None => null,
            _ => null,
        };
    }
}
