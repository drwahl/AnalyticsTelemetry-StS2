using System.IO;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

/// <summary>Persisted overlay / mod metrics UI toggles under user data.</summary>
internal static class TelemetryMetricsUiPreferences
{
    private const string Section = "metrics_ui";

    private static string PrefsPath =>
        Path.Combine(OS.GetUserDataDir(), "AnalyticsTelemetry", "metrics_ui.cfg");

    public static bool CompactPanel { get; set; }
    public static bool SleekCharts { get; set; }
    public static bool ChartHover { get; set; } = true;

    /// <summary>Live “Session throughput” chart (NDJSON Δ/sample) — mostly useful for mod debugging; off by default.</summary>
    public static bool ShowLiveThroughputChart { get; set; }

    /// <summary>Which lines appear on combined dmg in/out/block charts (live Δ, cumulative, 5m wall, replay).</summary>
    public static bool ShowChartDamageIn { get; set; } = true;
    public static bool ShowChartDamageOut { get; set; } = true;
    public static bool ShowChartDamageUnk { get; set; } = true;
    public static bool ShowChartBlock { get; set; } = true;

    public static MetricsVisualUiOptions PresentationOptions => new(CompactPanel, SleekCharts, ChartHover);

    public static void LoadFromDisk()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(PrefsPath) != Error.Ok)
            return;
        CompactPanel = (bool)cfg.GetValue(Section, "compact_panel", false);
        SleekCharts = (bool)cfg.GetValue(Section, "sleek_charts", false);
        ChartHover = (bool)cfg.GetValue(Section, "chart_hover", true);
        ShowLiveThroughputChart = (bool)cfg.GetValue(Section, "show_live_throughput", false);
        ShowChartDamageIn = (bool)cfg.GetValue(Section, "show_chart_dmg_in", true);
        ShowChartDamageOut = (bool)cfg.GetValue(Section, "show_chart_dmg_out", true);
        ShowChartDamageUnk = (bool)cfg.GetValue(Section, "show_chart_dmg_unk", true);
        ShowChartBlock = (bool)cfg.GetValue(Section, "show_chart_block", true);
        NormalizeDamageSeriesToggles();
    }

    /// <summary>At least one dmg line must stay on — empty chart is confusing.</summary>
    public static void NormalizeDamageSeriesToggles()
    {
        if (ShowChartDamageIn || ShowChartDamageOut || ShowChartDamageUnk || ShowChartBlock)
            return;
        ShowChartDamageIn = ShowChartDamageOut = ShowChartDamageUnk = ShowChartBlock = true;
    }

    public static void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cfg = new ConfigFile();
            cfg.SetValue(Section, "compact_panel", CompactPanel);
            cfg.SetValue(Section, "sleek_charts", SleekCharts);
            cfg.SetValue(Section, "chart_hover", ChartHover);
            cfg.SetValue(Section, "show_live_throughput", ShowLiveThroughputChart);
            cfg.SetValue(Section, "show_chart_dmg_in", ShowChartDamageIn);
            cfg.SetValue(Section, "show_chart_dmg_out", ShowChartDamageOut);
            cfg.SetValue(Section, "show_chart_dmg_unk", ShowChartDamageUnk);
            cfg.SetValue(Section, "show_chart_block", ShowChartBlock);
            cfg.Save(PrefsPath);
        }
        catch
        {
            // ignore disk errors
        }
    }
}
