using System.Security.Cryptography;
using System.Text;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Stable short pseudonymous keys for MP party members (not raw net IDs in NDJSON).</summary>
internal static class PlayerKeyUtil
{
    internal static string FromNetId(ulong netId)
    {
        var bytes = BitConverter.GetBytes(netId);
        var hash = SHA256.HashData(bytes);
        return "p_" + Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
