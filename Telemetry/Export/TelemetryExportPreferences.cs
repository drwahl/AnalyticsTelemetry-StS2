using System.IO;
using Godot;

namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>Persisted remote export settings under user data (same folder tree as NDJSON).</summary>
internal static class TelemetryExportPreferences
{
    private const string Section = "export";

    private static string PrefsPath =>
        Path.Combine(OS.GetUserDataDir(), "AnalyticsTelemetry", "export.cfg");

    public static bool RemoteEnabled { get; set; }

    /// <summary>Which backend to use when <see cref="RemoteEnabled"/> is true.</summary>
    public static TelemetryExportKind Kind { get; set; } = TelemetryExportKind.InfluxLineProtocolHttp;

    /// <summary>Full HTTP URL for writes (e.g. VictoriaMetrics <c>http://127.0.0.1:8428/write</c>).</summary>
    public static string InfluxWriteUrl { get; set; } = "";

    /// <summary>Optional <c>Authorization</c> header value (e.g. InfluxDB 2: <c>Token &lt;secret&gt;</c>).</summary>
    public static string? AuthorizationHeaderValue { get; set; }

    /// <summary>Influx line protocol measurement name (low cardinality).</summary>
    public static string InfluxMeasurement { get; set; } = "analytics_telemetry";

    public static int BatchMaxLines { get; set; } = 48;

    public static int BatchIntervalMs { get; set; } = 2000;

    public static void LoadFromDisk()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(PrefsPath) != Error.Ok)
            return;
        RemoteEnabled = (bool)cfg.GetValue(Section, "remote_enabled", false);
        Kind = (TelemetryExportKind)(int)(long)cfg.GetValue(Section, "kind", (long)TelemetryExportKind.InfluxLineProtocolHttp);
        InfluxWriteUrl = (string)cfg.GetValue(Section, "influx_write_url", "");
        AuthorizationHeaderValue = (string)cfg.GetValue(Section, "auth_header", "");
        InfluxMeasurement = (string)cfg.GetValue(Section, "influx_measurement", "analytics_telemetry");
        BatchMaxLines = (int)(long)cfg.GetValue(Section, "batch_max_lines", 48L);
        BatchIntervalMs = (int)(long)cfg.GetValue(Section, "batch_interval_ms", 2000L);
    }

    public static void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cfg = new ConfigFile();
            cfg.SetValue(Section, "remote_enabled", RemoteEnabled);
            cfg.SetValue(Section, "kind", (long)Kind);
            cfg.SetValue(Section, "influx_write_url", InfluxWriteUrl ?? "");
            cfg.SetValue(Section, "auth_header", AuthorizationHeaderValue ?? "");
            cfg.SetValue(Section, "influx_measurement", string.IsNullOrWhiteSpace(InfluxMeasurement) ? "analytics_telemetry" : InfluxMeasurement);
            cfg.SetValue(Section, "batch_max_lines", BatchMaxLines);
            cfg.SetValue(Section, "batch_interval_ms", BatchIntervalMs);
            cfg.Save(PrefsPath);
        }
        catch
        {
            // ignore disk errors
        }
    }
}
