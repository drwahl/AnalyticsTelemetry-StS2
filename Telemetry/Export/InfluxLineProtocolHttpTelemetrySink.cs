using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Channels;
using AnalyticsTelemetry.AnalyticsTelemetryCode;

namespace AnalyticsTelemetry.Telemetry.Export;

/// <summary>
/// Batched HTTP POST of InfluxDB line protocol lines. Compatible with VictoriaMetrics <c>/write</c> and common Influx write endpoints.
/// </summary>
internal sealed class InfluxLineProtocolHttpTelemetrySink : ITelemetryEventSink
{
    private readonly Uri _endpoint;
    private readonly string _measurement;
    private readonly int _batchMax;
    private readonly int _batchIntervalMs;
    private readonly string? _authHeaderValue;
    private readonly HttpClient _http = new();
    private readonly Channel<PendingLine> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private InfluxLineProtocolHttpTelemetrySink(
        Uri endpoint,
        string measurement,
        int batchMax,
        int batchIntervalMs,
        string? authHeaderValue)
    {
        SinkId = "influx_line_http";
        _endpoint = endpoint;
        _measurement = measurement;
        _batchMax = Math.Max(1, batchMax);
        _batchIntervalMs = Math.Max(100, batchIntervalMs);
        _authHeaderValue = string.IsNullOrWhiteSpace(authHeaderValue) ? null : authHeaderValue.Trim();
        if (_authHeaderValue is not null)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _authHeaderValue);

        _channel = Channel.CreateBounded<PendingLine>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _loop = Task.Run(RunAsync);
    }

    public static InfluxLineProtocolHttpTelemetrySink? TryCreate()
    {
        var raw = TelemetryExportPreferences.InfluxWriteUrl?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
            return null;
        }

        var m = TelemetryExportPreferences.InfluxMeasurement?.Trim() ?? "analytics_telemetry";
        if (string.IsNullOrEmpty(m))
            m = "analytics_telemetry";

        return new InfluxLineProtocolHttpTelemetrySink(
            uri,
            m,
            TelemetryExportPreferences.BatchMaxLines,
            TelemetryExportPreferences.BatchIntervalMs,
            TelemetryExportPreferences.AuthorizationHeaderValue);
    }

    public string SinkId { get; }

    public void Write(in TelemetryEnvelope envelope, string ndjsonLine)
    {
        var payloadJson = envelope.Payload.GetRawText();
        var line = BuildLine(_measurement, envelope.EventType, envelope.Seq, envelope.OccurredUtc, payloadJson);
        _channel.Writer.TryWrite(new PendingLine(line));
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
        }
        catch
        {
            // ignore
        }

        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        _http.Dispose();
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            PendingLine first;
            try
            {
                first = await reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(_batchIntervalMs);
            var batch = new StringBuilder(8192);
            AppendLine(batch, first.Line);
            var n = 1;
            while (n < _batchMax && DateTime.UtcNow < deadline && reader.TryRead(out var next))
            {
                AppendLine(batch, next.Line);
                n++;
            }

            try
            {
                using var content = new StringContent(batch.ToString(), Encoding.UTF8, "text/plain");
                using var resp = await _http.PostAsync(_endpoint, content, token).ConfigureAwait(false);
                _ = resp;
            }
            catch
            {
                // Swallow network errors; game must not crash on telemetry export.
            }
        }

        try
        {
            var tail = new StringBuilder();
            while (reader.TryRead(out var item))
                AppendLine(tail, item.Line);

            if (tail.Length > 0)
            {
                using var content = new StringContent(tail.ToString(), Encoding.UTF8, "text/plain");
                using var resp = await _http.PostAsync(_endpoint, content, CancellationToken.None).ConfigureAwait(false);
                _ = resp;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.Append(line);
        if (!line.EndsWith('\n'))
            sb.Append('\n');
    }

    private readonly record struct PendingLine(string Line);

    private static string BuildLine(
        string measurement,
        string eventType,
        long seq,
        DateTime occurredUtc,
        string payloadJson)
    {
        var modId = MainFile.ModId;
        var sb = new StringBuilder(measurement.Length + eventType.Length + payloadJson.Length + 64);
        sb.Append(measurement);
        sb.Append(",mod_id=").Append(EscapeTag(modId));
        sb.Append(",event_type=").Append(EscapeTag(SanitizeEventTypeTag(eventType)));
        sb.Append(" seq=").Append(seq.ToString(CultureInfo.InvariantCulture)).Append('i');
        sb.Append(",payload=").Append(EscapeFieldString(payloadJson));
        var ns = new DateTimeOffset(DateTime.SpecifyKind(occurredUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            * 1_000_000L;
        sb.Append(' ').Append(ns.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Influx line protocol: tag keys/values — escape \, space, comma, equals.</summary>
    private static string EscapeTag(string s)
    {
        return s
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal);
    }

    private static string SanitizeEventTypeTag(string eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return "unknown";
        Span<char> buf = stackalloc char[Math.Min(eventType.Length, 96)];
        var n = 0;
        foreach (var c in eventType)
        {
            if (n >= buf.Length)
                break;
            buf[n++] = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'
                ? c
                : '_';
        }

        return new string(buf[..n]);
    }

    /// <summary>Double-quoted field; escape \ and ".</summary>
    private static string EscapeFieldString(string s)
    {
        var escaped = s
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
