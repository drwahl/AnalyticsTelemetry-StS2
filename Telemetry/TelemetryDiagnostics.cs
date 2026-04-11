using System.Reflection;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>
/// One-shot runtime assembly identity logging for troubleshooting load / binding issues (e.g. BaseLib NuGet vs <c>mods/BaseLib</c> version mismatch).
/// </summary>
internal static class TelemetryDiagnostics
{
    private const string Prefix = "AnalyticsTelemetry diagnostics:";

    /// <summary>Log resolved assemblies to the game log (search for <see cref="Prefix"/> in <c>godot*.log</c>).</summary>
    public static void LogRuntimeAssemblies(Logger logger)
    {
        try
        {
            logger.Info($"{Prefix} mod assembly = {FormatAssembly(typeof(TelemetryEventLog).Assembly)} | path = {SafeLocation(typeof(TelemetryEventLog).Assembly)}");
            logger.Info($"{Prefix} BaseLib (from typeof(ModConfigRegistry)) = {FormatAssembly(typeof(ModConfigRegistry).Assembly)} | path = {SafeLocation(typeof(ModConfigRegistry).Assembly)}");
            logger.Info($"{Prefix} sts2 (from typeof(ModInitializerAttribute)) = {FormatAssembly(typeof(ModInitializerAttribute).Assembly)} | path = {SafeLocation(typeof(ModInitializerAttribute).Assembly)}");
            logger.Info($"{Prefix} 0Harmony = {FormatAssembly(typeof(Harmony).Assembly)} | path = {SafeLocation(typeof(Harmony).Assembly)}");
        }
        catch (Exception e)
        {
            logger.Warn($"{Prefix} failed to enumerate host assemblies: {e}");
        }
    }

    /// <summary>Embeds host reference identities into <c>session_start</c> NDJSON for offline support bundles.</summary>
    public static HostReferenceSnapshot CaptureHostReferences()
    {
        try
        {
            return new HostReferenceSnapshot(
                FormatAssembly(typeof(ModConfigRegistry).Assembly),
                SafeLocation(typeof(ModConfigRegistry).Assembly),
                FormatAssembly(typeof(ModInitializerAttribute).Assembly),
                SafeLocation(typeof(ModInitializerAttribute).Assembly),
                FormatAssembly(typeof(Harmony).Assembly),
                SafeLocation(typeof(Harmony).Assembly));
        }
        catch
        {
            return new HostReferenceSnapshot(null, null, null, null, null, null);
        }
    }

    private static string FormatAssembly(Assembly a) => a.GetName().FullName;

    private static string? SafeLocation(Assembly a)
    {
        try
        {
            var p = a.Location;
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record HostReferenceSnapshot(
    string? BaseLibAssemblyFullName,
    string? BaseLibPath,
    string? Sts2AssemblyFullName,
    string? Sts2Path,
    string? HarmonyAssemblyFullName,
    string? HarmonyPath);
