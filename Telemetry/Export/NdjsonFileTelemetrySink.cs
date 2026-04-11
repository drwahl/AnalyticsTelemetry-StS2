using System.Text;
using Godot;

namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>Append-only NDJSON session file (primary sink).</summary>
internal sealed class NdjsonFileTelemetrySink : ITelemetryEventSink
{
    private readonly StreamWriter _writer;

    public NdjsonFileTelemetrySink(string modId, out string sessionPath)
    {
        SinkId = "ndjson_file";
        var root = System.IO.Path.Combine(OS.GetUserDataDir(), modId);
        var sessions = System.IO.Path.Combine(root, "sessions");
        System.IO.Directory.CreateDirectory(sessions);

        sessionPath = System.IO.Path.Combine(sessions, $"run-{Guid.NewGuid():N}.ndjson");
        SessionPath = sessionPath;
        _writer = new StreamWriter(
            new System.IO.FileStream(
                sessionPath,
                System.IO.FileMode.CreateNew,
                System.IO.FileAccess.Write,
                System.IO.FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public string SinkId { get; }

    public string SessionPath { get; }

    public void Write(in TelemetryEnvelope envelope, string ndjsonLine)
    {
        _writer.WriteLine(ndjsonLine);
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
