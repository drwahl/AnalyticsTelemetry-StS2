using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;

namespace AnalyticsTelemetry.Telemetry;

internal enum MetricsDrillView
{
    Overview = 0,
    Run = 1,
    Act = 2,
    Combat = 3,
    Hands = 4,
    Multiplayer = 5,
}

/// <summary>Who lost HP in a <c>combat_history_damage_received</c> line (best-effort string heuristics on logged properties).</summary>
internal enum DamageReceivedVictimKind
{
    Unknown = 0,
    ToPlayer = 1,
    ToEnemy = 2,
}

/// <summary>Hierarchical rollups for the overlay + same events as NDJSON (session, act, combat, hand, MP).</summary>
internal static class TelemetryMetricsStore
{
    private static readonly object Gate = new();

    private static readonly MetricsAccumulator Session = new();
    private static readonly Dictionary<string, MetricsAccumulator> ByAct = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, MetricsAccumulator> ByCombat = new();
    private static readonly Dictionary<string, MetricsAccumulator> ByPlayerSession = new(StringComparer.Ordinal);
    private static readonly List<HandRollup> HandHistory = [];
    private const int MaxHands = 36;
    /// <summary>Damage parsed from history since the last <c>combat_player_energy_turn</c>; flushed into that hand’s rollup.</summary>
    private static decimal _pendingHandDamageLive;

    private static readonly List<float> SeriesElapsedSec = [];
    private static readonly List<long> SeriesEventsDelta = [];
    private static readonly List<long> SeriesHistoryDelta = [];
    private static readonly List<long> SeriesPlaysDelta = [];
    private static readonly List<long> SeriesDrawsDelta = [];
    private static DateTime _seriesOriginUtc;
    private static DateTime _lastSeriesSampleUtc;
    private static int _eventsSinceSeriesSample;
    private static long _seriesPrevEvents;
    private static long _seriesPrevHistory;
    private static long _seriesPrevPlays;
    private static long _seriesPrevDraws;
    private static long _seriesPrevDiscards;
    private static long _seriesPrevExhausts;
    private static long _seriesPrevGenerated;
    private static decimal _seriesPrevDamageSum;
    private static decimal _seriesPrevDamageIn;
    private static decimal _seriesPrevDamageOut;
    private static decimal _seriesPrevDamageUnk;
    private static decimal _seriesPrevBlockSum;
    private static decimal _seriesPrevEnergyGain;
    private static decimal _seriesPrevEnergyLose;
    private static long _seriesPrevEnergySteps;
    private static int _seriesPrevEnergyTurns;
    private static long _seriesPrevStatusToPlayer;
    private static long _seriesPrevStatusToEnemy;
    private static long _seriesPrevStatusUnknown;

    private static readonly List<double> SeriesEnergyTurnsDelta = [];
    /// <summary>Session-wide running totals at each live chart sample (same length as *Delta lists).</summary>
    private static readonly List<double> SeriesSnapEvents = [];
    private static readonly List<double> SeriesSnapHistory = [];
    private static readonly List<double> SeriesSnapPlays = [];
    private static readonly List<double> SeriesSnapDraws = [];
    private static readonly List<double> SeriesSnapDiscards = [];
    private static readonly List<double> SeriesSnapExhausts = [];
    private static readonly List<double> SeriesSnapGenerated = [];
    private static readonly List<double> SeriesSnapDamageSum = [];
    private static readonly List<double> SeriesSnapDamageIn = [];
    private static readonly List<double> SeriesSnapDamageOut = [];
    private static readonly List<double> SeriesSnapDamageUnk = [];
    private static readonly List<double> SeriesSnapBlockSum = [];
    private static readonly List<double> SeriesSnapEnergyGain = [];
    private static readonly List<double> SeriesSnapEnergyLose = [];
    private static readonly List<double> SeriesSnapEnergySteps = [];
    private static readonly List<double> SeriesSnapEnergyTurns = [];
    private static readonly List<double> SeriesGoldDelta = [];
    private static readonly List<double> SeriesSnapGold = [];
    private static double _seriesPrevGoldChart;
    private static double _liveGoldLevelHeld;

    private static readonly List<long> SeriesDiscardsDelta = [];
    private static readonly List<long> SeriesExhaustsDelta = [];
    private static readonly List<long> SeriesGeneratedDelta = [];
    private static readonly List<double> SeriesDamageDelta = [];
    private static readonly List<double> SeriesDamageInDelta = [];
    private static readonly List<double> SeriesDamageOutDelta = [];
    private static readonly List<double> SeriesDamageUnkDelta = [];
    private static readonly List<double> SeriesBlockDelta = [];
    private static readonly List<double> SeriesEnergyGainDelta = [];
    private static readonly List<double> SeriesEnergyLoseDelta = [];
    private static readonly List<double> SeriesEnergyStepsDelta = [];
    private static readonly List<double> SeriesStatusToPlayerDelta = [];
    private static readonly List<double> SeriesStatusToEnemyDelta = [];
    private static readonly List<double> SeriesStatusUnknownDelta = [];
    private static readonly List<double> SeriesSnapStatusToPlayer = [];
    private static readonly List<double> SeriesSnapStatusToEnemy = [];
    private static readonly List<double> SeriesSnapStatusUnknown = [];

    /// <summary>Wall-clock 5-minute buckets: Σ damage/block deltas that fell in each interval (first sample sets origin).</summary>
    private static DateTime _liveWallBucketOriginUtc;
    private static readonly List<double> LiveWall5mDamageIn = [];
    private static readonly List<double> LiveWall5mDamageOut = [];
    private static readonly List<double> LiveWall5mDamageUnk = [];
    private static readonly List<double> LiveWall5mBlock = [];

    /// <summary>Per chart sample: attributed card damage Δ since previous sample (same length as <see cref="SeriesDamageDelta"/> when primed).</summary>
    private static readonly List<Dictionary<string, double>> LiveCardDeltaSamples = [];
    private static readonly Dictionary<string, decimal> LiveCardDamagePrevSample = new(StringComparer.Ordinal);
    private static bool _liveCardDeltaPrimed;

    private static readonly string[] DamageAmountPropertyKeys =
    [
        "Amount", "Damage", "DamageAmount", "HitDamage", "FinalDamage", "Value", "DamageDealt",
        "OutgoingDamage", "TotalDamage", "DealtDamage", "RawDamage", "FinalDamageAmount",
        "HitPointsRemoved", "HealthLost", "HpLost", "Magnitude", "Loss", "TrueDamage",
    ];

    private static readonly string[] BlockAmountPropertyKeys =
    [
        "Amount", "Block", "BlockAmount", "GainedBlock", "Value",
        "TotalBlock", "AddedBlock", "BlockAdded", "Barrier", "ShieldAmount", "MitigatedAmount",
    ];

    private static readonly string[] DamagePropertyNameHints =
    [
        "damage", "dmg", "hit", "harm", "attack", "dealt", "hurt",
    ];

    private static readonly string[] BlockPropertyNameHints =
    [
        "block", "shield", "guard", "mitigat", "ward", "absorb", "barrier",
    ];

    /// <summary>Shared line-chart colors — periwinkle block still read as “light blue” on some displays; lime + magenta separate clearly from coral in.</summary>
    private static readonly Color ChartColorDamageIn = new(1f, 0.35f, 0.32f);
    private static readonly Color ChartColorDamageOut = new(0.22f, 0.95f, 0.42f);
    private static readonly Color ChartColorDamageUnk = new(0.62f, 0.62f, 0.70f);
    private static readonly Color ChartColorBlock = new(0.98f, 0.32f, 0.72f);
    private static readonly Color ChartColorBlockRatio = new(0.95f, 0.62f, 0.38f);
    private static readonly Color ChartColorGold = new(0.95f, 0.78f, 0.28f);
    private static readonly Color ChartColorGoldDelta = new(0.98f, 0.55f, 0.22f);

    private static string DamageChartFilterFootnote()
    {
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn && TelemetryMetricsUiPreferences.ShowChartDamageOut
            && TelemetryMetricsUiPreferences.ShowChartDamageUnk && TelemetryMetricsUiPreferences.ShowChartBlock)
            return "";
        var parts = new List<string>(4);
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn)
            parts.Add("in");
        if (TelemetryMetricsUiPreferences.ShowChartDamageOut)
            parts.Add("out");
        if (TelemetryMetricsUiPreferences.ShowChartDamageUnk)
            parts.Add("unk");
        if (TelemetryMetricsUiPreferences.ShowChartBlock)
            parts.Add("block");
        return " Showing: " + string.Join(", ", parts) + " — toggles in Analytics overlay or Mod → Live metrics.";
    }

    private static List<MetricTimeSeries> BuildLiveDamageDeltaSeriesLocked()
    {
        var list = new List<MetricTimeSeries>(4);
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn)
            list.Add(new("Dmg in Δ (to player)", ChartColorDamageIn, SeriesDamageInDelta.ToArray(), SeriesSnapDamageIn.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartDamageOut)
            list.Add(new("Dmg out Δ (to enemies)", ChartColorDamageOut, SeriesDamageOutDelta.ToArray(), SeriesSnapDamageOut.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartDamageUnk)
            list.Add(new("Dmg unclassified Δ", ChartColorDamageUnk, SeriesDamageUnkDelta.ToArray(), SeriesSnapDamageUnk.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartBlock)
            list.Add(new("Block Δ", ChartColorBlock, SeriesBlockDelta.ToArray(), SeriesSnapBlockSum.ToArray()));
        if (list.Count == 0)
        {
            list.Add(new("Dmg in Δ (to player)", ChartColorDamageIn, SeriesDamageInDelta.ToArray(), SeriesSnapDamageIn.ToArray()));
            list.Add(new("Dmg out Δ (to enemies)", ChartColorDamageOut, SeriesDamageOutDelta.ToArray(), SeriesSnapDamageOut.ToArray()));
            list.Add(new("Dmg unclassified Δ", ChartColorDamageUnk, SeriesDamageUnkDelta.ToArray(), SeriesSnapDamageUnk.ToArray()));
            list.Add(new("Block Δ", ChartColorBlock, SeriesBlockDelta.ToArray(), SeriesSnapBlockSum.ToArray()));
        }

        return list;
    }

    private static List<MetricTimeSeries> BuildLiveDamageCumulativeSeriesLocked()
    {
        var list = new List<MetricTimeSeries>(4);
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn)
            list.Add(new("Dmg in Σ (to player)", ChartColorDamageIn, SeriesSnapDamageIn.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartDamageOut)
            list.Add(new("Dmg out Σ (to enemies)", ChartColorDamageOut, SeriesSnapDamageOut.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartDamageUnk)
            list.Add(new("Dmg unclassified Σ", ChartColorDamageUnk, SeriesSnapDamageUnk.ToArray()));
        if (TelemetryMetricsUiPreferences.ShowChartBlock)
            list.Add(new("Block Σ", ChartColorBlock, SeriesSnapBlockSum.ToArray()));
        if (list.Count == 0)
        {
            list.Add(new("Dmg in Σ (to player)", ChartColorDamageIn, SeriesSnapDamageIn.ToArray()));
            list.Add(new("Dmg out Σ (to enemies)", ChartColorDamageOut, SeriesSnapDamageOut.ToArray()));
            list.Add(new("Dmg unclassified Σ", ChartColorDamageUnk, SeriesSnapDamageUnk.ToArray()));
            list.Add(new("Block Σ", ChartColorBlock, SeriesSnapBlockSum.ToArray()));
        }

        return list;
    }

    private static List<MetricTimeSeries> BuildReplayDamageWallSeries(int n, double[] dmgInB, double[] dmgOutB, double[] dmgUnkB, double[] blkB)
    {
        double[] Slice(double[] src)
        {
            var a = new double[n];
            for (var i = 0; i < n; i++)
                a[i] = src[i];
            return a;
        }

        var list = new List<MetricTimeSeries>(4);
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn)
            list.Add(new MetricTimeSeries("Dmg in (to player)", ChartColorDamageIn, Slice(dmgInB)));
        if (TelemetryMetricsUiPreferences.ShowChartDamageOut)
            list.Add(new MetricTimeSeries("Dmg out (to enemies)", ChartColorDamageOut, Slice(dmgOutB)));
        if (TelemetryMetricsUiPreferences.ShowChartDamageUnk)
            list.Add(new MetricTimeSeries("Dmg unclassified", ChartColorDamageUnk, Slice(dmgUnkB)));
        if (TelemetryMetricsUiPreferences.ShowChartBlock)
            list.Add(new MetricTimeSeries("Block (parsed Σ)", ChartColorBlock, Slice(blkB)));
        if (list.Count == 0)
        {
            list.Add(new MetricTimeSeries("Dmg in (to player)", ChartColorDamageIn, Slice(dmgInB)));
            list.Add(new MetricTimeSeries("Dmg out (to enemies)", ChartColorDamageOut, Slice(dmgOutB)));
            list.Add(new MetricTimeSeries("Dmg unclassified", ChartColorDamageUnk, Slice(dmgUnkB)));
            list.Add(new MetricTimeSeries("Block (parsed Σ)", ChartColorBlock, Slice(blkB)));
        }

        return list;
    }

    internal static void Reset()
    {
        CardDamageAttributionTracker.ResetSession();
        lock (Gate)
        {
            Session.Clear();
            ByAct.Clear();
            ByCombat.Clear();
            ByPlayerSession.Clear();
            HandHistory.Clear();
            _pendingHandDamageLive = 0;
            ClearLiveSeriesLocked();
        }
    }

    /// <summary>
    /// Clears live rollups, chart buffers, staged energy-flow steps, and the in-process replay aggregate cache.
    /// Does not delete NDJSON on disk, change <see cref="TelemetryScopeContext"/>, or rotate the log file.
    /// </summary>
    internal static void ClearAllHistoricMetrics()
    {
        CombatEnergyFlowTracker.Reset();
        Reset();
        lock (HistoricalGate)
        {
            _histAcc = null;
            _histHands = null;
            _histByAct = null;
            _histByCombat = null;
            _histCharts = null;
            _histFingerprint = "";
            _histCacheTicksMs = 0;
        }
    }

    /// <summary>Live combat only — attributes damage to the active card-play bracket.</summary>
    internal static void ApplyAttributedCardDamage(string cardKey, decimal amount)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(cardKey))
            return;
        lock (Gate)
        {
            Session.AddCardDamage(cardKey, amount);
            var scope = TelemetryScopeContext.Snapshot();
            var actKey = ActKey(scope);
            if (!string.IsNullOrEmpty(actKey))
                Bucket(ByAct, actKey).AddCardDamage(cardKey, amount);
            if (scope.CombatOrdinal > 0)
                Bucket(ByCombat, scope.CombatOrdinal).AddCardDamage(cardKey, amount);
        }
    }

    /// <summary>
    /// Live combat: damage that did not fall under an open card-play bracket (poison ticks, thorns, etc.).
    /// <paramref name="kindLabel"/> is a short bucket from <see cref="PassiveDamageLabel.Build"/> (e.g. Poison); stored as Passive:Poison in <see cref="MetricsAccumulator.DamageByCard"/>.
    /// </summary>
    internal static void ApplyPassiveDamage(string kindLabel, decimal amount)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(kindLabel))
            return;
        lock (Gate)
        {
            Session.AddPassiveAttributedDamage(kindLabel, amount);
            var scope = TelemetryScopeContext.Snapshot();
            var actKey = ActKey(scope);
            if (!string.IsNullOrEmpty(actKey))
                Bucket(ByAct, actKey).AddPassiveAttributedDamage(kindLabel, amount);
            if (scope.CombatOrdinal > 0)
                Bucket(ByCombat, scope.CombatOrdinal).AddPassiveAttributedDamage(kindLabel, amount);
        }
    }

    private static void ClearLiveSeriesLocked()
    {
        SeriesElapsedSec.Clear();
        SeriesEventsDelta.Clear();
        SeriesHistoryDelta.Clear();
        SeriesPlaysDelta.Clear();
        SeriesDrawsDelta.Clear();
        SeriesDiscardsDelta.Clear();
        SeriesExhaustsDelta.Clear();
        SeriesGeneratedDelta.Clear();
        SeriesDamageDelta.Clear();
        SeriesDamageInDelta.Clear();
        SeriesDamageOutDelta.Clear();
        SeriesDamageUnkDelta.Clear();
        SeriesBlockDelta.Clear();
        SeriesEnergyGainDelta.Clear();
        SeriesEnergyLoseDelta.Clear();
        SeriesEnergyStepsDelta.Clear();
        SeriesEnergyTurnsDelta.Clear();
        SeriesStatusToPlayerDelta.Clear();
        SeriesStatusToEnemyDelta.Clear();
        SeriesStatusUnknownDelta.Clear();
        SeriesSnapStatusToPlayer.Clear();
        SeriesSnapStatusToEnemy.Clear();
        SeriesSnapStatusUnknown.Clear();
        SeriesSnapEvents.Clear();
        SeriesSnapHistory.Clear();
        SeriesSnapPlays.Clear();
        SeriesSnapDraws.Clear();
        SeriesSnapDiscards.Clear();
        SeriesSnapExhausts.Clear();
        SeriesSnapGenerated.Clear();
        SeriesSnapDamageSum.Clear();
        SeriesSnapDamageIn.Clear();
        SeriesSnapDamageOut.Clear();
        SeriesSnapDamageUnk.Clear();
        SeriesSnapBlockSum.Clear();
        SeriesSnapEnergyGain.Clear();
        SeriesSnapEnergyLose.Clear();
        SeriesSnapEnergySteps.Clear();
        SeriesSnapEnergyTurns.Clear();
        SeriesGoldDelta.Clear();
        SeriesSnapGold.Clear();
        _seriesPrevGoldChart = 0;
        _liveGoldLevelHeld = 0;
        LiveWall5mDamageIn.Clear();
        LiveWall5mDamageOut.Clear();
        LiveWall5mDamageUnk.Clear();
        LiveWall5mBlock.Clear();
        _liveWallBucketOriginUtc = default;
        LiveCardDeltaSamples.Clear();
        LiveCardDamagePrevSample.Clear();
        _liveCardDeltaPrimed = false;
        _seriesOriginUtc = default;
        _lastSeriesSampleUtc = default;
        _eventsSinceSeriesSample = 0;
        _seriesPrevEvents = 0;
        _seriesPrevHistory = 0;
        _seriesPrevPlays = 0;
        _seriesPrevDraws = 0;
        _seriesPrevDiscards = 0;
        _seriesPrevExhausts = 0;
        _seriesPrevGenerated = 0;
        _seriesPrevDamageSum = 0;
        _seriesPrevDamageIn = 0;
        _seriesPrevDamageOut = 0;
        _seriesPrevDamageUnk = 0;
        _seriesPrevBlockSum = 0;
        _seriesPrevEnergyGain = 0;
        _seriesPrevEnergyLose = 0;
        _seriesPrevEnergySteps = 0;
        _seriesPrevEnergyTurns = 0;
        _seriesPrevStatusToPlayer = 0;
        _seriesPrevStatusToEnemy = 0;
        _seriesPrevStatusUnknown = 0;
    }

    private static void MaybeAppendLiveTimeSeriesSampleLocked()
    {
        var now = DateTime.UtcNow;
        if (_seriesOriginUtc == default)
            _seriesOriginUtc = now;
        _eventsSinceSeriesSample++;
        var dt = (now - _lastSeriesSampleUtc).TotalSeconds;
        if (SeriesElapsedSec.Count > 0 && dt < 1.5 && _eventsSinceSeriesSample < 24)
            return;
        _lastSeriesSampleUtc = now;
        _eventsSinceSeriesSample = 0;
        var ev = Session.Events;
        var hi = Session.CombatHistoryLines;
        var pl = Session.Plays;
        var dr = Session.Draws;
        var di = Session.Discards;
        var ex = Session.Exhausts;
        var ge = Session.Generated;
        var dmg = Session.DamageDealtSum;
        var dmgIn = Session.DamageReceivedToPlayerSum;
        var dmgOut = Session.DamageReceivedToEnemySum;
        var dmgUnk = Session.DamageReceivedUnknownSum;
        var blk = Session.BlockGainedSum;
        var eg = Session.EnergyGainSum;
        var el = Session.EnergyLoseSum;
        var est = Session.EnergySteps;
        var stPl = Session.StatusEffectOnPlayerEvents;
        var stEn = Session.StatusEffectOnEnemyEvents;
        var stUn = Session.StatusEffectUnknownRecipientEvents;
        SeriesElapsedSec.Add((float)(now - _seriesOriginUtc).TotalSeconds);
        SeriesEventsDelta.Add(ev - _seriesPrevEvents);
        SeriesHistoryDelta.Add(hi - _seriesPrevHistory);
        SeriesPlaysDelta.Add(pl - _seriesPrevPlays);
        SeriesDrawsDelta.Add(dr - _seriesPrevDraws);
        SeriesDiscardsDelta.Add(di - _seriesPrevDiscards);
        SeriesExhaustsDelta.Add(ex - _seriesPrevExhausts);
        SeriesGeneratedDelta.Add(ge - _seriesPrevGenerated);
        var dmgInDelta = (double)(dmgIn - _seriesPrevDamageIn);
        var dmgOutDelta = (double)(dmgOut - _seriesPrevDamageOut);
        var dmgUnkDelta = (double)(dmgUnk - _seriesPrevDamageUnk);
        var dmgDelta = dmgInDelta + dmgOutDelta + dmgUnkDelta;
        var blkDelta = (double)(blk - _seriesPrevBlockSum);
        SeriesDamageDelta.Add(dmgDelta);
        SeriesDamageInDelta.Add(dmgInDelta);
        SeriesDamageOutDelta.Add(dmgOutDelta);
        SeriesDamageUnkDelta.Add(dmgUnkDelta);
        SeriesBlockDelta.Add(blkDelta);

        if (_liveWallBucketOriginUtc == default)
            _liveWallBucketOriginUtc = now;
        var bucket = (int)((now - _liveWallBucketOriginUtc).TotalMinutes / 5.0);
        if (bucket < 0)
            bucket = 0;
        while (LiveWall5mDamageIn.Count <= bucket)
        {
            LiveWall5mDamageIn.Add(0);
            LiveWall5mDamageOut.Add(0);
            LiveWall5mDamageUnk.Add(0);
            LiveWall5mBlock.Add(0);
        }

        LiveWall5mDamageIn[bucket] += dmgInDelta;
        LiveWall5mDamageOut[bucket] += dmgOutDelta;
        LiveWall5mDamageUnk[bucket] += dmgUnkDelta;
        LiveWall5mBlock[bucket] += blkDelta;

        AppendLiveCardDeltaSampleLocked();
        SeriesEnergyGainDelta.Add((double)(eg - _seriesPrevEnergyGain));
        SeriesEnergyLoseDelta.Add((double)(el - _seriesPrevEnergyLose));
        SeriesEnergyStepsDelta.Add(est - _seriesPrevEnergySteps);
        SeriesEnergyTurnsDelta.Add(Session.EnergyTurns - _seriesPrevEnergyTurns);
        SeriesStatusToPlayerDelta.Add(stPl - _seriesPrevStatusToPlayer);
        SeriesStatusToEnemyDelta.Add(stEn - _seriesPrevStatusToEnemy);
        SeriesStatusUnknownDelta.Add(stUn - _seriesPrevStatusUnknown);
        SeriesSnapEvents.Add(ev);
        SeriesSnapHistory.Add(hi);
        SeriesSnapPlays.Add(pl);
        SeriesSnapDraws.Add(dr);
        SeriesSnapDiscards.Add(di);
        SeriesSnapExhausts.Add(ex);
        SeriesSnapGenerated.Add(ge);
        SeriesSnapDamageSum.Add((double)dmg);
        SeriesSnapDamageIn.Add((double)dmgIn);
        SeriesSnapDamageOut.Add((double)dmgOut);
        SeriesSnapDamageUnk.Add((double)dmgUnk);
        SeriesSnapBlockSum.Add((double)blk);
        SeriesSnapEnergyGain.Add((double)eg);
        SeriesSnapEnergyLose.Add((double)el);
        SeriesSnapEnergySteps.Add(est);
        SeriesSnapEnergyTurns.Add(Session.EnergyTurns);
        SeriesSnapStatusToPlayer.Add(stPl);
        SeriesSnapStatusToEnemy.Add(stEn);
        SeriesSnapStatusUnknown.Add(stUn);
        if (Session.LastGold is { } gl)
            _liveGoldLevelHeld = gl;
        var goldLevel = Session.LastGold.HasValue ? Session.LastGold.Value : _liveGoldLevelHeld;
        var goldDelta = goldLevel - _seriesPrevGoldChart;
        SeriesGoldDelta.Add(goldDelta);
        SeriesSnapGold.Add(goldLevel);
        _seriesPrevGoldChart = goldLevel;
        _seriesPrevEvents = ev;
        _seriesPrevHistory = hi;
        _seriesPrevPlays = pl;
        _seriesPrevDraws = dr;
        _seriesPrevDiscards = di;
        _seriesPrevExhausts = ex;
        _seriesPrevGenerated = ge;
        _seriesPrevDamageSum = dmg;
        _seriesPrevDamageIn = dmgIn;
        _seriesPrevDamageOut = dmgOut;
        _seriesPrevDamageUnk = dmgUnk;
        _seriesPrevBlockSum = blk;
        _seriesPrevEnergyGain = eg;
        _seriesPrevEnergyLose = el;
        _seriesPrevEnergySteps = est;
        _seriesPrevEnergyTurns = Session.EnergyTurns;
        _seriesPrevStatusToPlayer = stPl;
        _seriesPrevStatusToEnemy = stEn;
        _seriesPrevStatusUnknown = stUn;
        const int cap = 360;
        while (SeriesElapsedSec.Count > cap)
        {
            SeriesElapsedSec.RemoveAt(0);
            SeriesEventsDelta.RemoveAt(0);
            SeriesHistoryDelta.RemoveAt(0);
            SeriesPlaysDelta.RemoveAt(0);
            SeriesDrawsDelta.RemoveAt(0);
            SeriesDiscardsDelta.RemoveAt(0);
            SeriesExhaustsDelta.RemoveAt(0);
            SeriesGeneratedDelta.RemoveAt(0);
            SeriesDamageDelta.RemoveAt(0);
            SeriesDamageInDelta.RemoveAt(0);
            SeriesDamageOutDelta.RemoveAt(0);
            SeriesDamageUnkDelta.RemoveAt(0);
            SeriesBlockDelta.RemoveAt(0);
            SeriesEnergyGainDelta.RemoveAt(0);
            SeriesEnergyLoseDelta.RemoveAt(0);
            SeriesEnergyStepsDelta.RemoveAt(0);
            SeriesEnergyTurnsDelta.RemoveAt(0);
            SeriesStatusToPlayerDelta.RemoveAt(0);
            SeriesStatusToEnemyDelta.RemoveAt(0);
            SeriesStatusUnknownDelta.RemoveAt(0);
            SeriesSnapStatusToPlayer.RemoveAt(0);
            SeriesSnapStatusToEnemy.RemoveAt(0);
            SeriesSnapStatusUnknown.RemoveAt(0);
            SeriesSnapEvents.RemoveAt(0);
            SeriesSnapHistory.RemoveAt(0);
            SeriesSnapPlays.RemoveAt(0);
            SeriesSnapDraws.RemoveAt(0);
            SeriesSnapDiscards.RemoveAt(0);
            SeriesSnapExhausts.RemoveAt(0);
            SeriesSnapGenerated.RemoveAt(0);
            SeriesSnapDamageSum.RemoveAt(0);
            SeriesSnapDamageIn.RemoveAt(0);
            SeriesSnapDamageOut.RemoveAt(0);
            SeriesSnapDamageUnk.RemoveAt(0);
            SeriesSnapBlockSum.RemoveAt(0);
            SeriesSnapEnergyGain.RemoveAt(0);
            SeriesSnapEnergyLose.RemoveAt(0);
            SeriesSnapEnergySteps.RemoveAt(0);
            SeriesSnapEnergyTurns.RemoveAt(0);
            if (SeriesGoldDelta.Count > 0)
                SeriesGoldDelta.RemoveAt(0);
            if (SeriesSnapGold.Count > 0)
                SeriesSnapGold.RemoveAt(0);
            if (LiveCardDeltaSamples.Count > 0)
                LiveCardDeltaSamples.RemoveAt(0);
        }
    }

    private static void AppendLiveCardDeltaSampleLocked()
    {
        if (!_liveCardDeltaPrimed)
        {
            LiveCardDamagePrevSample.Clear();
            foreach (var kv in Session.DamageByCard)
                LiveCardDamagePrevSample[kv.Key] = kv.Value;
            _liveCardDeltaPrimed = true;
            LiveCardDeltaSamples.Add(new Dictionary<string, double>(StringComparer.Ordinal));
            return;
        }

        var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kv in Session.DamageByCard)
        {
            var prev = LiveCardDamagePrevSample.GetValueOrDefault(kv.Key);
            var d = (double)(kv.Value - prev);
            if (Math.Abs(d) > 1e-9)
                deltas[kv.Key] = d;
        }

        LiveCardDamagePrevSample.Clear();
        foreach (var kv in Session.DamageByCard)
            LiveCardDamagePrevSample[kv.Key] = kv.Value;
        LiveCardDeltaSamples.Add(deltas);
    }

    private static IReadOnlyList<MetricTimeSeriesChart> LiveTimeSeriesChartsLocked(MetricsDrillView drillView)
    {
        if (SeriesElapsedSec.Count < 1)
            return Array.Empty<MetricTimeSeriesChart>();
        const string liveHover =
            "Live: line height = change since last sample (~1.5s wall or burst). Hover shows session running total and that change.";
        const string liveDeltaDmgHover =
            "Live: from combat_history_damage_received (HP lost by a victim) and block_gained — not “attack cards only.” Red/coral = dmg to player (in), lime = dmg to enemies (out), gray = unclassified, magenta = block Δ per ~1.5s sample.";
        const string liveCumulativeHover =
            "Live: running Σ — dmg in (player), dmg out (enemies), unclassified, then block. Same log sources and colors as the Δ chart above.";
        const string liveDerivedHover =
            "Live: per sample bucket, derived ratios from the same Δs as other charts (not comparable to replay 5‑min buckets).";
        var throughput = new List<MetricTimeSeries>(4)
        {
            new("NDJSON events", new Color(0.52f, 0.78f, 1f), SeriesEventsDelta.Select(z => (double)z).ToArray(),
                SeriesSnapEvents.ToArray()),
            new("Combat history lines", new Color(0.45f, 0.88f, 0.58f), SeriesHistoryDelta.Select(z => (double)z).ToArray(),
                SeriesSnapHistory.ToArray()),
            new("Card plays", new Color(0.32f, 0.9f, 0.52f), SeriesPlaysDelta.Select(z => (double)z).ToArray(),
                SeriesSnapPlays.ToArray()),
            new("Card draws", new Color(0.42f, 0.68f, 1f), SeriesDrawsDelta.Select(z => (double)z).ToArray(),
                SeriesSnapDraws.ToArray()),
        };
        var damageBlock = BuildLiveDamageDeltaSeriesLocked();
        var damageBlockCumulative = BuildLiveDamageCumulativeSeriesLocked();
        var dmgFoot = DamageChartFilterFootnote();
        var energy = new List<MetricTimeSeries>(4)
        {
            new("Player energy turns (count)", new Color(1f, 0.92f, 0.45f), SeriesEnergyTurnsDelta.ToArray(),
                SeriesSnapEnergyTurns.ToArray()),
            new("Energy steps (Σ)", new Color(0.5f, 0.92f, 0.72f), SeriesEnergyStepsDelta.ToArray(), SeriesSnapEnergySteps.ToArray()),
            new("Energy gained (Σ)", new Color(0.95f, 0.82f, 0.35f), SeriesEnergyGainDelta.ToArray(), SeriesSnapEnergyGain.ToArray()),
            new("Energy lost (Σ)", new Color(0.9f, 0.5f, 0.35f), SeriesEnergyLoseDelta.ToArray(), SeriesSnapEnergyLose.ToArray()),
        };
        var piles = new List<MetricTimeSeries>(4)
        {
            new("Draws", new Color(0.42f, 0.68f, 1f), SeriesDrawsDelta.Select(z => (double)z).ToArray(), SeriesSnapDraws.ToArray()),
            new("Discards", new Color(1f, 0.72f, 0.28f), SeriesDiscardsDelta.Select(z => (double)z).ToArray(),
                SeriesSnapDiscards.ToArray()),
            new("Exhaust", new Color(1f, 0.42f, 0.38f), SeriesExhaustsDelta.Select(z => (double)z).ToArray(),
                SeriesSnapExhausts.ToArray()),
            new("Generated", new Color(0.82f, 0.5f, 1f), SeriesGeneratedDelta.Select(z => (double)z).ToArray(),
                SeriesSnapGenerated.ToArray()),
        };

        var charts = new List<MetricTimeSeriesChart>();
        if (TelemetryMetricsUiPreferences.ShowLiveThroughputChart)
        {
            charts.Add(new MetricTimeSeriesChart(
                "Session throughput (live, debug)",
                throughput,
                liveHover + " This chart is mainly for verifying the logger samples; turn it on in Analytics overlay or Mod settings."));
        }

        charts.Add(new MetricTimeSeriesChart("Dmg in / out / block Δ (live, ~1.5s samples)", damageBlock, liveDeltaDmgHover + dmgFoot));
        charts.Add(new MetricTimeSeriesChart("Dmg in / out / block cumulative (live)", damageBlockCumulative, liveCumulativeHover + dmgFoot));
        charts.AddRange(BuildLiveFiveMinuteWallChartsLocked());
        charts.AddRange(BuildLiveCardDamageDeltaChartsLocked());
        charts.Add(new MetricTimeSeriesChart("Player energy (live)", energy, liveHover));
        const string liveGoldHover =
            "Live: gold total is read from current_run.save (best-effort JSON keys). Δ = change vs previous ~1.5s chart sample; "
            + "run_gold NDJSON lines fire when the save’s gold field changes (often every few seconds in-run).";
        var goldSeries = new List<MetricTimeSeries>(2)
        {
            new("Gold Δ / sample", ChartColorGoldDelta, SeriesGoldDelta.ToArray()),
            new("Gold total (held)", ChartColorGold, SeriesSnapGold.ToArray(), SeriesSnapGold.ToArray()),
        };
        charts.Add(new MetricTimeSeriesChart("Gold (run save, live)", goldSeries, liveGoldHover));
        charts.Add(new MetricTimeSeriesChart("Card pile events (live — not hand sizes)", piles, liveHover));
        const string liveStatusHover =
            "Live: counts of combat_history_power_received + card_afflicted lines, bucketed by inferred recipient (heuristic on logged properties). Δ chart uses the same ~1.5s samples as damage. Hover shows running Σ per line.";
        var statusDelta = new List<MetricTimeSeries>(3)
        {
            new("Status lines → player Δ", new Color(1f, 0.55f, 0.42f), SeriesStatusToPlayerDelta.ToArray(), SeriesSnapStatusToPlayer.ToArray()),
            new("Status lines → enemies Δ", new Color(0.45f, 0.88f, 0.55f), SeriesStatusToEnemyDelta.ToArray(), SeriesSnapStatusToEnemy.ToArray()),
            new("Status lines unknown Δ", new Color(0.72f, 0.72f, 0.78f), SeriesStatusUnknownDelta.ToArray(), SeriesSnapStatusUnknown.ToArray()),
        };
        charts.Add(new MetricTimeSeriesChart("Powers & afflictions Δ (live)", statusDelta, liveStatusHover));

        var n = SeriesPlaysDelta.Count;
        if (n >= 1)
        {
            var playsPerEnergy = new double[n];
            var dmgPerPlay = new double[n];
            var netPile = new double[n];
            var blockPerDmg = new double[n];
            for (var i = 0; i < n; i++)
            {
                var et = Math.Max(1, SeriesEnergyTurnsDelta[i]);
                playsPerEnergy[i] = (double)SeriesPlaysDelta[i] / et;
                var pl = Math.Max(1, SeriesPlaysDelta[i]);
                dmgPerPlay[i] = SeriesDamageDelta[i] / pl;
                netPile[i] = SeriesDrawsDelta[i] - (double)SeriesDiscardsDelta[i] - SeriesExhaustsDelta[i];
                blockPerDmg[i] = SeriesDamageDelta[i] > 1e-9 ? SeriesBlockDelta[i] / SeriesDamageDelta[i] : 0;
            }

            charts.Add(new MetricTimeSeriesChart(
                "Tempo & conversion (live)",
                [
                    new MetricTimeSeries("Plays / energy turn (Δ ratio)", new Color(0.45f, 0.95f, 0.55f), playsPerEnergy),
                    new MetricTimeSeries("Damage Δ / plays Δ", new Color(1f, 0.5f, 0.4f), dmgPerPlay),
                    new MetricTimeSeries("Net draws−disc−exh (Δ)", new Color(0.55f, 0.7f, 1f), netPile),
                    new MetricTimeSeries("Block Δ / damage Δ", ChartColorBlockRatio, blockPerDmg),
                ],
                liveDerivedHover));
        }

        if (HandHistory.Count > 0)
            charts.AddRange(BuildLiveHandDetailChartsLocked());

        return charts;
    }

    private static IReadOnlyList<MetricTimeSeriesChart> BuildLiveFiveMinuteWallChartsLocked()
    {
        if (LiveWall5mDamageIn.Count < 1)
            return Array.Empty<MetricTimeSeriesChart>();
        const string hover =
            "Live: X = 5-minute wall-clock bucket index (0 = first 5 min since first chart sample). Y = Σ of parsed deltas in that interval (dmg in / out / unclassified + block).";
        var n = LiveWall5mDamageIn.Count;
        var dIn = new double[n];
        var dOut = new double[n];
        var dUnk = new double[n];
        var blk = new double[n];
        for (var i = 0; i < n; i++)
        {
            dIn[i] = LiveWall5mDamageIn[i];
            dOut[i] = LiveWall5mDamageOut[i];
            dUnk[i] = LiveWall5mDamageUnk[i];
            blk[i] = LiveWall5mBlock[i];
        }

        var wallSeries = new List<MetricTimeSeries>(4);
        if (TelemetryMetricsUiPreferences.ShowChartDamageIn)
            wallSeries.Add(new MetricTimeSeries("Dmg in (5m bucket)", ChartColorDamageIn, dIn));
        if (TelemetryMetricsUiPreferences.ShowChartDamageOut)
            wallSeries.Add(new MetricTimeSeries("Dmg out (5m bucket)", ChartColorDamageOut, dOut));
        if (TelemetryMetricsUiPreferences.ShowChartDamageUnk)
            wallSeries.Add(new MetricTimeSeries("Dmg unclassified (5m)", ChartColorDamageUnk, dUnk));
        if (TelemetryMetricsUiPreferences.ShowChartBlock)
            wallSeries.Add(new MetricTimeSeries("Block (5m bucket)", ChartColorBlock, blk));
        if (wallSeries.Count == 0)
        {
            wallSeries.Add(new MetricTimeSeries("Dmg in (5m bucket)", ChartColorDamageIn, dIn));
            wallSeries.Add(new MetricTimeSeries("Dmg out (5m bucket)", ChartColorDamageOut, dOut));
            wallSeries.Add(new MetricTimeSeries("Dmg unclassified (5m)", ChartColorDamageUnk, dUnk));
            wallSeries.Add(new MetricTimeSeries("Block (5m bucket)", ChartColorBlock, blk));
        }

        return
        [
            new MetricTimeSeriesChart(
                "Dmg in / out / block (live, per 5 min wall clock)",
                wallSeries,
                hover + DamageChartFilterFootnote()),
        ];
    }

    private static IReadOnlyList<MetricTimeSeriesChart> BuildLiveCardDamageDeltaChartsLocked()
    {
        if (LiveCardDeltaSamples.Count < 1)
            return Array.Empty<MetricTimeSeriesChart>();
        var n = LiveCardDeltaSamples.Count;
        var keyScores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in LiveCardDeltaSamples)
        {
            foreach (var kv in sample)
                keyScores[kv.Key] = keyScores.GetValueOrDefault(kv.Key) + kv.Value;
        }

        var top = keyScores
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => kv.Key)
            .ToList();
        if (top.Count == 0)
            return Array.Empty<MetricTimeSeriesChart>();
        var colors = new[]
        {
            new Color(1f, 0.45f, 0.38f),
            new Color(0.95f, 0.65f, 0.35f),
            new Color(0.55f, 0.88f, 0.48f),
            new Color(0.5f, 0.75f, 1f),
            new Color(0.85f, 0.55f, 1f),
        };
        var series = new List<MetricTimeSeries>(top.Count);
        for (var i = 0; i < top.Count; i++)
        {
            var arr = new double[n];
            for (var j = 0; j < n; j++)
                arr[j] = LiveCardDeltaSamples[j].GetValueOrDefault(top[i]);
            var label = top[i].Length <= 42 ? top[i] : top[i][..39] + "…";
            series.Add(new MetricTimeSeries(label, colors[i % colors.Length], arr));
        }

        return
        [
            new MetricTimeSeriesChart(
                "Damage Δ by card (live, play-attributed)",
                series,
                "Live: Y = attributed card damage gained since the previous ~1.5s chart sample (same cadence as Δ damage chart). Top 5 cards by ΣΔ over the window."),
        ];
    }

    /// <summary>Recent <see cref="HandHistory"/> as time series (all drills when hands exist).</summary>
    private static IReadOnlyList<MetricTimeSeriesChart> BuildLiveHandDetailChartsLocked()
    {
        const int maxPts = 72;
        const string handHover =
            "Live: each X = one completed player energy turn (combat_player_energy_turn), oldest → newest in window. Shown whenever hand data exists (all drill tabs).";
        var slice = HandHistory.Count <= maxPts
            ? HandHistory.ToList()
            : HandHistory.GetRange(HandHistory.Count - maxPts, maxPts);
        var dmg = slice.Select(h => (double)h.DamageInHand).ToArray();
        var steps = slice.Select(h => (double)h.Steps).ToArray();
        var gain = slice.Select(h => (double)h.Gain).ToArray();
        var lose = slice.Select(h => (double)h.Lose).ToArray();
        if (dmg.Length < 1)
            return Array.Empty<MetricTimeSeriesChart>();
        var roll2 = RollingHandSum(dmg, 2);
        var roll3 = RollingHandSum(dmg, 3);
        return
        [
            new MetricTimeSeriesChart(
                "Damage by hand (live, recent)",
                [new MetricTimeSeries("Damage (Σ / hand)", new Color(1f, 0.45f, 0.38f), dmg)],
                handHover),
            new MetricTimeSeriesChart(
                "Energy motion by hand (live)",
                [
                    new MetricTimeSeries("Steps / hand", new Color(0.55f, 0.92f, 0.72f), steps),
                    new MetricTimeSeries("Energy + (hand)", new Color(0.95f, 0.82f, 0.35f), gain),
                    new MetricTimeSeries("Energy − (hand)", new Color(0.9f, 0.5f, 0.35f), lose),
                ],
                handHover),
            new MetricTimeSeriesChart(
                "Damage rolling windows (live)",
                [
                    new MetricTimeSeries("Last 2 hands Σ", new Color(1f, 0.62f, 0.42f), roll2),
                    new MetricTimeSeries("Last 3 hands Σ", new Color(0.95f, 0.5f, 0.55f), roll3),
                ],
                handHover + " Rolling lines sum the current hand and the previous N−1 hands."),
        ];
    }

    /// <summary>Same as <see cref="BuildLiveHandDetailChartsLocked"/> but only hands tagged with <paramref name="combatOrdinal"/>.</summary>
    private static IReadOnlyList<MetricTimeSeriesChart> BuildLiveHandDetailChartsForCombatLocked(int combatOrdinal)
    {
        const int maxPts = 72;
        const string handHover =
            "Live: each X = one completed player energy turn in this combat only (combat_player_energy_turn), oldest → newest in window.";
        var filtered = new List<HandRollup>();
        foreach (var h in HandHistory)
        {
            if (h.CombatOrdinal == combatOrdinal)
                filtered.Add(h);
        }

        if (filtered.Count == 0)
        {
            return
            [
                new MetricTimeSeriesChart(
                    $"Hands in combat #{combatOrdinal} (live)",
                    [new MetricTimeSeries("(no turns in buffer)", new Color(0.55f, 0.55f, 0.6f), new double[] { 0 })],
                    handHover + " Buffer keeps recent turns session-wide; none tagged for this combat ordinal yet."),
            ];
        }

        var slice = filtered.Count <= maxPts
            ? filtered
            : filtered.GetRange(filtered.Count - maxPts, maxPts);
        var dmg = slice.Select(h => (double)h.DamageInHand).ToArray();
        var steps = slice.Select(h => (double)h.Steps).ToArray();
        var gain = slice.Select(h => (double)h.Gain).ToArray();
        var lose = slice.Select(h => (double)h.Lose).ToArray();
        var roll2 = RollingHandSum(dmg, 2);
        var roll3 = RollingHandSum(dmg, 3);
        return
        [
            new MetricTimeSeriesChart(
                $"Damage by hand (live, combat #{combatOrdinal})",
                [new MetricTimeSeries("Damage (Σ / hand)", new Color(1f, 0.45f, 0.38f), dmg)],
                handHover),
            new MetricTimeSeriesChart(
                $"Energy motion by hand (live, combat #{combatOrdinal})",
                [
                    new MetricTimeSeries("Steps / hand", new Color(0.55f, 0.92f, 0.72f), steps),
                    new MetricTimeSeries("Energy + (hand)", new Color(0.95f, 0.82f, 0.35f), gain),
                    new MetricTimeSeries("Energy − (hand)", new Color(0.9f, 0.5f, 0.35f), lose),
                ],
                handHover),
            new MetricTimeSeriesChart(
                $"Damage rolling windows (live, combat #{combatOrdinal})",
                [
                    new MetricTimeSeries("Last 2 hands Σ", new Color(1f, 0.62f, 0.42f), roll2),
                    new MetricTimeSeries("Last 3 hands Σ", new Color(0.95f, 0.5f, 0.55f), roll3),
                ],
                handHover + " Rolling lines sum the current hand and the previous N−1 hands."),
        ];
    }

    /// <summary>
    /// Combat scope hides session-wide throughput (debug) when present; other charts stay.
    /// </summary>
    private static IReadOnlyList<MetricTimeSeriesChart> FilterTimeSeriesChartsForDrill(
        IReadOnlyList<MetricTimeSeriesChart> charts,
        MetricsDrillView view)
    {
        if (charts.Count == 0)
            return charts;
        if (view != MetricsDrillView.Combat)
            return charts;
        return charts
            .Where(c => !c.SectionTitle.StartsWith("Session throughput", StringComparison.Ordinal))
            .ToList();
    }

    private static bool ChartsHaveAnyPoint(IReadOnlyList<MetricTimeSeriesChart> charts) =>
        charts.Any(c => c.Series.Any(s => s.Values.Count >= 1));

    /// <summary>Parses damage from combat_history_damage_received payload.properties (names vary by game build).</summary>
    private static bool TryParseDamageFromHistoryPayload(JsonElement payload, out decimal value) =>
        TryExtractHistoryDecimal(payload, DamageAmountPropertyKeys, DamagePropertyNameHints, out value);

    /// <summary>
    /// <see cref="DamageReceivedEntry"/> logs HP removed for some victim — not “damage you dealt.”
    /// We split heuristically for charts; unknown lines stay in <see cref="DamageReceivedVictimKind.Unknown"/>.
    /// </summary>
    private static DamageReceivedVictimKind ClassifyDamageReceivedVictimKind(JsonElement payload)
    {
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return DamageReceivedVictimKind.Unknown;
        var sb = new StringBuilder(384);
        foreach (var p in props.EnumerateObject())
        {
            sb.Append(p.Name);
            sb.Append('=');
            if (p.Value.ValueKind == JsonValueKind.String)
                sb.Append(p.Value.GetString());
            else
                sb.Append(p.Value.ToString());
            sb.Append(';');
        }

        var blob = sb.ToString();
        if (blob.Length == 0)
            return DamageReceivedVictimKind.Unknown;
        if (BlobSuggestsPlayerVictim(blob))
            return DamageReceivedVictimKind.ToPlayer;
        if (BlobSuggestsEnemyVictim(blob))
            return DamageReceivedVictimKind.ToEnemy;
        return DamageReceivedVictimKind.Unknown;
    }

    internal static bool BlobSuggestsPlayerVictim(string blob)
    {
        if (blob.Contains("Player", StringComparison.OrdinalIgnoreCase))
            return true;
        ReadOnlySpan<string> names =
        [
            "Ironclad", "Silent", "Defect", "Watcher", "BasePlayer", "PlayerEntity", "HumanPlayer",
        ];
        foreach (var n in names)
        {
            if (blob.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool BlobSuggestsEnemyVictim(string blob)
    {
        ReadOnlySpan<string> names =
        [
            "Monster", "Enemy", "Boss", "Minion", "Mob", "Creature",
        ];
        foreach (var n in names)
        {
            if (blob.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Parses block from combat_history_block_gained payload.properties.</summary>
    private static bool TryParseBlockFromHistoryPayload(JsonElement payload, out decimal value) =>
        TryExtractHistoryDecimal(payload, BlockAmountPropertyKeys, BlockPropertyNameHints, out value);

    private static bool TryExtractHistoryDecimal(
        JsonElement payload,
        string[] preferredKeyOrder,
        string[] propertyNameContainsHints,
        out decimal value)
    {
        value = 0;
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var key in preferredKeyOrder)
        {
            if (TryGetDecimalFromHistoryProperty(props, key, out var d))
            {
                value = d;
                return true;
            }
        }

        decimal bestHint = 0;
        var anyHint = false;
        foreach (var prop in props.EnumerateObject())
        {
            foreach (var hint in propertyNameContainsHints)
            {
                if (prop.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!TryParseJsonNumberLike(prop.Value, out var d) || d <= 0)
                    continue;
                if (!anyHint || d > bestHint)
                {
                    bestHint = d;
                    anyHint = true;
                }

                break;
            }
        }

        if (anyHint)
        {
            value = bestHint;
            return true;
        }

        // Last resort: if there is exactly one small positive number among non-ID properties, use it.
        var candidates = new List<decimal>(8);
        foreach (var prop in props.EnumerateObject())
        {
            if (IsLikelyNonNumericHistoryProperty(prop.Name))
                continue;
            if (!TryParseJsonNumberLike(prop.Value, out var d) || d <= 0 || d > 99_999m)
                continue;
            candidates.Add(d);
        }

        if (candidates.Count != 1)
            return false;
        value = candidates[0];
        return true;
    }

    private static bool IsLikelyNonNumericHistoryProperty(string name)
    {
        if (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("NetId", StringComparison.Ordinal))
            return true;
        if (NameContainsCi(name, "Ordinal") || NameContainsCi(name, "Sequence"))
            return true;
        if (NameContainsCi(name, "Guid") || NameContainsCi(name, "Hash"))
            return true;
        return false;
    }

    private static bool NameContainsCi(string name, string sub) =>
        name.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryGetDecimalFromHistoryProperty(JsonElement props, string name, out decimal d)
    {
        d = 0;
        if (props.TryGetProperty(name, out var direct) && TryParseJsonNumberLike(direct, out d))
            return true;
        foreach (var prop in props.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            return TryParseJsonNumberLike(prop.Value, out d);
        }

        return false;
    }

    private static bool TryParseJsonNumberLike(JsonElement el, out decimal d)
    {
        d = 0;
        if (el.ValueKind == JsonValueKind.Number)
        {
            try
            {
                d = el.GetDecimal();
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (el.ValueKind != JsonValueKind.String)
            return false;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return true;
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d);
    }

    private static List<int> ListCombatOrdinalsOrderedLocked() =>
        ByCombat.Keys.Order().ToList();

    internal static IReadOnlyList<int> ListCombatOrdinalsForUi()
    {
        lock (Gate)
            return ListCombatOrdinalsOrderedLocked();
    }

    internal static IReadOnlyList<string> ListActKeysForUi()
    {
        lock (Gate)
            return ListActKeysOrderedLocked();
    }

    private static List<string> ListActKeysOrderedLocked() =>
        ByAct.Keys.OrderBy(ParseActKeyOrdinal).ThenBy(k => k, StringComparer.Ordinal).ToList();

    private static int ParseActKeyOrdinal(string key)
    {
        var i = key.IndexOf(':');
        if (i <= 0)
            return int.MaxValue;
        return int.TryParse(key.AsSpan(0, i), System.Globalization.NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v)
            ? v
            : int.MaxValue;
    }

    private static string? TryActKeyFromPayload(JsonElement payload)
    {
        if (!TryGetInt(payload, "actIndex", out var ai) || ai < 0)
            return null;
        var id = TryGetString(payload, "actId") ?? "";
        return $"{ai}:{ShortAct(id)}";
    }

    private static bool TryParseRoomVisitsFromPayload(JsonElement payload, out Dictionary<string, int> visits)
    {
        visits = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!payload.TryGetProperty("roomVisitsByType", out var el) || el.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n))
                visits[prop.Name] = n;
        }

        return visits.Count > 0;
    }

    internal static void RecordEvent(string eventType, JsonElement payload)
    {
        var scope = TelemetryScopeContext.Snapshot();
        lock (Gate)
        {
            Session.Add(eventType, payload);
            if (eventType == "combat_history_damage_received"
                && TryParseDamageFromHistoryPayload(payload, out var handDmg))
                _pendingHandDamageLive += handDmg;
            if (eventType == "combat_started")
                _pendingHandDamageLive = 0;
            var actKey = ActKey(scope);
            if (!string.IsNullOrEmpty(actKey))
                Bucket(ByAct, actKey).Add(eventType, payload, ingestRunContextRoomVisits: false);

            if (scope.CombatOrdinal > 0)
                Bucket(ByCombat, scope.CombatOrdinal).Add(eventType, payload, ingestRunContextRoomVisits: false);

            var hk = TryHistoryPlayerKey(payload);
            if (hk is not null)
                Bucket(ByPlayerSession, hk).Add(eventType, payload, ingestRunContextRoomVisits: false);

            if (eventType == "combat_player_energy_turn")
            {
                RecordHandRollup(payload, scope, _pendingHandDamageLive);
                _pendingHandDamageLive = 0;
            }

            MaybeAppendLiveTimeSeriesSampleLocked();
        }
    }

    private static void RecordHandRollup(JsonElement payload, TelemetryScopeSnapshot scope, decimal damageInHand)
    {
        TryGetInt(payload, "combatOrdinal", out var cOrd);
        if (cOrd <= 0)
            cOrd = scope.CombatOrdinal;
        TryGetInt(payload, "handSequence", out var hSeq);
        TryGetDecimalFirst(payload, out var g, "totalGain", "TotalGain", "total_gain");
        TryGetDecimalFirst(payload, out var l, "totalLose", "TotalLose", "total_lose");
        TryGetIntFirst(payload, out var sets, "setOperationCount", "SetOperationCount", "set_operation_count");
        TryGetIntFirst(payload, out var steps, "stepCount", "StepCount", "step_count");
        var pk = TryGetString(payload, "playerKey");

        HandHistory.Add(new HandRollup(cOrd, hSeq, pk, steps, g, l, sets, damageInHand));
        while (HandHistory.Count > MaxHands)
            HandHistory.RemoveAt(0);

        if (pk is not null)
        {
            var acc = Bucket(ByPlayerSession, pk);
            acc.EnergyTurns++;
            acc.EnergySteps += steps;
            acc.EnergyGainSum += g;
            acc.EnergyLoseSum += l;
            acc.EnergySetOps += sets;
        }
    }

    internal static string FormatView(MetricsDrillView view)
    {
        var scope = TelemetryScopeContext.Snapshot();
        lock (Gate)
        {
            return view switch
            {
                MetricsDrillView.Overview => FormatOverview(scope),
                MetricsDrillView.Run => FormatRun(scope),
                MetricsDrillView.Act => FormatAct(scope),
                MetricsDrillView.Combat => FormatCombat(scope),
                MetricsDrillView.Hands => FormatHands(),
                MetricsDrillView.Multiplayer => FormatMultiplayer(scope),
                _ => FormatOverview(scope),
            };
        }
    }

    /// <summary>Structured view for charts / counter grids (overlay + mod settings).</summary>
    internal static MetricsVisualModel BuildVisualModel(MetricsDrillView view)
    {
        var scope = TelemetryScopeContext.Snapshot();
        lock (Gate)
        {
            var liveCharts = FilterTimeSeriesChartsForDrill(LiveTimeSeriesChartsLocked(view), view);
            var livePrefer = ChartsHaveAnyPoint(liveCharts);
            return view switch
            {
                MetricsDrillView.Overview => BuildVisualOverview(scope, liveCharts, livePrefer),
                MetricsDrillView.Run => BuildVisualRun(scope, liveCharts, livePrefer),
                MetricsDrillView.Act => BuildVisualAct(scope, liveCharts, livePrefer),
                MetricsDrillView.Combat => BuildVisualCombat(scope, liveCharts, livePrefer),
                MetricsDrillView.Hands => BuildVisualHands(liveCharts, livePrefer),
                MetricsDrillView.Multiplayer => BuildVisualMultiplayer(scope, liveCharts, livePrefer),
                _ => BuildVisualOverview(scope, liveCharts, livePrefer),
            };
        }
    }

    private static readonly object HistoricalGate = new();
    private static TelemetryDatasetSelection _histSel = TelemetryDatasetSelection.Current;
    private static string _histFingerprint = "";
    private static ulong _histCacheTicksMs;
    private const ulong HistoricalTtlMs = 2000;
    private static MetricsAccumulator? _histAcc;
    private static List<HandRollup>? _histHands;
    private static Dictionary<string, MetricsAccumulator>? _histByAct;
    private static Dictionary<int, MetricsAccumulator>? _histByCombat;
    private static List<MetricTimeSeriesChart>? _histCharts;

    internal static IReadOnlyList<string> ListActKeysForUi(TelemetryDatasetSelection dataset)
    {
        if (dataset.Kind == TelemetryDatasetKind.CurrentSession)
            return ListActKeysForUi();
        lock (HistoricalGate)
        {
            if (_histByAct is null || _histByAct.Count == 0)
                return Array.Empty<string>();
            return _histByAct.Keys.OrderBy(ParseActKeyOrdinal).ThenBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    internal static IReadOnlyList<int> ListCombatOrdinalsForUi(TelemetryDatasetSelection dataset)
    {
        if (dataset.Kind == TelemetryDatasetKind.CurrentSession)
            return ListCombatOrdinalsForUi();
        lock (HistoricalGate)
        {
            if (_histByCombat is null || _histByCombat.Count == 0)
                return Array.Empty<int>();
            return _histByCombat.Keys.Order().ToList();
        }
    }

    /// <summary>Live session, or replayed NDJSON aggregate when <paramref name="dataset"/> is not current.</summary>
    internal static MetricsVisualModel BuildVisualModel(MetricsDrillView view, TelemetryDatasetSelection dataset)
    {
        if (dataset.Kind == TelemetryDatasetKind.CurrentSession)
            return BuildVisualModel(view);

        lock (HistoricalGate)
        {
            var paths = TelemetryDatasetCatalog.ResolvePaths(dataset, out var summary);
            var fp =
                $"{(int)dataset.Kind}|{dataset.SingleFileFullPath ?? ""}|{TelemetryDatasetCatalog.FingerprintReplaySources(paths)}";
            var now = Godot.Time.GetTicksMsec();
            var cacheOk = _histAcc is not null
                && fp == _histFingerprint
                && dataset.Equals(_histSel)
                && now - _histCacheTicksMs < HistoricalTtlMs;

            if (!cacheOk)
            {
                _histSel = dataset;
                _histFingerprint = fp;
                _histCacheTicksMs = now;
                (_histAcc, _histByAct, _histByCombat, _histHands, _histCharts) = AggregateFromNdjson(paths);
            }

            return BuildVisualMerged(
                view,
                _histAcc!,
                _histByAct ?? new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal),
                _histByCombat ?? new Dictionary<int, MetricsAccumulator>(),
                _histHands!,
                _histCharts ?? [],
                summary);
        }
    }

    private static (
        MetricsAccumulator Session,
        Dictionary<string, MetricsAccumulator> ByAct,
        Dictionary<int, MetricsAccumulator> ByCombat,
        List<HandRollup> Hands,
        List<MetricTimeSeriesChart> Charts) AggregateFromNdjson(IReadOnlyList<string> paths)
    {
        var session = new MetricsAccumulator();
        var byAct = new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal);
        var byCombat = new Dictionary<int, MetricsAccumulator>();
        var hands = new List<HandRollup>(256);
        string? actTag = null;
        var replayCombatOrdinal = 0;
        var eB = new long[64];
        var hB = new long[64];
        var pB = new long[64];
        var dB = new long[64];
        var dmgInB = new double[64];
        var dmgOutB = new double[64];
        var dmgUnkB = new double[64];
        var blkB = new double[64];
        var eGainB = new double[64];
        var eLoseB = new double[64];
        var eStepB = new double[64];
        var eTurnB = new long[64];
        var discB = new long[64];
        var exhB = new long[64];
        var genB = new long[64];
        var killsB = new long[64];
        var ttkSumB = new double[64];
        var ttkCountB = new long[64];
        var stPlB = new double[64];
        var stEnB = new double[64];
        var stUnkB = new double[64];
        DateTime? t0 = null;
        var mergedReplayRoomVisits = new Dictionary<string, long>(StringComparer.Ordinal);
        var cardReplay = new CardPlayReplayStacks();
        var pendingReplayHandDamage = 0m;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                cardReplay.Clear();
                pendingReplayHandDamage = 0m;
                Dictionary<string, int>? lastRoomVisitsInFile = null;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("eventType", out var et) || et.ValueKind != JsonValueKind.String)
                        continue;
                    var eventType = et.GetString();
                    if (string.IsNullOrEmpty(eventType))
                        continue;
                    if (!root.TryGetProperty("payload", out var payload))
                        continue;

                    if (eventType == "combat_started")
                        pendingReplayHandDamage = 0m;

                    if (eventType == "run_context_snapshot" || eventType == "combat_started")
                    {
                        var k = TryActKeyFromPayload(payload);
                        if (!string.IsNullOrEmpty(k))
                            actTag = k;
                    }

                    if (eventType == "combat_started" || eventType == "combat_ended")
                    {
                        if (TryGetInt(payload, "combatOrdinal", out var coStart) && coStart > 0)
                            replayCombatOrdinal = coStart;
                    }

                    session.Add(eventType, payload, ingestRunContextRoomVisits: false);
                    if (eventType == "run_context_snapshot" && TryParseRoomVisitsFromPayload(payload, out var rv))
                        lastRoomVisitsInFile = rv;

                    if (!string.IsNullOrEmpty(actTag))
                        Bucket(byAct, actTag).Add(eventType, payload, ingestRunContextRoomVisits: false);

                    var coLine = 0;
                    if (TryGetInt(payload, "combatOrdinal", out var coPayload) && coPayload > 0)
                        coLine = coPayload;
                    else if (replayCombatOrdinal > 0)
                        coLine = replayCombatOrdinal;
                    if (coLine > 0)
                        Bucket(byCombat, coLine).Add(eventType, payload, ingestRunContextRoomVisits: false);

                    TryApplyReplayCardAttribution(eventType, payload, coLine, actTag, cardReplay, session, byAct, byCombat);

                    if (eventType == "combat_history_damage_received"
                        && TryParseDamageFromHistoryPayload(payload, out var replayDmg))
                        pendingReplayHandDamage += replayDmg;

                    if (eventType == "combat_player_energy_turn")
                    {
                        AppendHistoricalHand(hands, payload, pendingReplayHandDamage);
                        pendingReplayHandDamage = 0m;
                    }

                    if (TryParseEnvelopeUtc(root, out var utc))
                    {
                        t0 ??= utc;
                        var bi = (int)((utc - t0.Value).TotalMinutes / 5.0);
                        if (bi >= 0 && bi < 64)
                        {
                            eB[bi]++;
                            if (eventType.StartsWith("combat_history_", StringComparison.Ordinal))
                                hB[bi]++;
                            if (eventType == "combat_history_card_play_started")
                                pB[bi]++;
                            if (eventType == "combat_history_card_drawn")
                                dB[bi]++;
                            if (eventType == "combat_history_card_discarded")
                                discB[bi]++;
                            if (eventType == "combat_history_card_exhausted")
                                exhB[bi]++;
                            if (eventType == "combat_history_card_generated")
                                genB[bi]++;
                            if (eventType == "combat_history_damage_received"
                                && TryParseDamageFromHistoryPayload(payload, out var dam))
                            {
                                switch (ClassifyDamageReceivedVictimKind(payload))
                                {
                                    case DamageReceivedVictimKind.ToPlayer:
                                        dmgInB[bi] += (double)dam;
                                        break;
                                    case DamageReceivedVictimKind.ToEnemy:
                                        dmgOutB[bi] += (double)dam;
                                        break;
                                    default:
                                        dmgUnkB[bi] += (double)dam;
                                        break;
                                }
                            }
                            if (eventType == "combat_history_block_gained"
                                && TryParseBlockFromHistoryPayload(payload, out var blk))
                                blkB[bi] += (double)blk;
                            if (eventType is "combat_history_power_received" or "combat_history_card_afflicted"
                                && CombatHistoryStatusEffectMetrics.TryDeriveFromJson(payload, eventType, out var stRec,
                                    out _, out _))
                            {
                                switch (stRec)
                                {
                                    case StatusEffectRecipientKind.ToPlayer:
                                        stPlB[bi]++;
                                        break;
                                    case StatusEffectRecipientKind.ToEnemy:
                                        stEnB[bi]++;
                                        break;
                                    default:
                                        stUnkB[bi]++;
                                        break;
                                }
                            }

                            if (eventType == "combat_player_energy_turn")
                            {
                                eTurnB[bi]++;
                                if (TryGetDecimalFirst(payload, out var tg, "totalGain", "TotalGain", "total_gain"))
                                    eGainB[bi] += (double)tg;
                                if (TryGetDecimalFirst(payload, out var tl, "totalLose", "TotalLose", "total_lose"))
                                    eLoseB[bi] += (double)tl;
                                if (TryGetIntFirst(payload, out var sc, "stepCount", "StepCount", "step_count"))
                                    eStepB[bi] += sc;
                            }

                            if (eventType == "combat_enemy_defeated")
                            {
                                killsB[bi]++;
                                if (payload.TryGetProperty("timeToKillSeconds", out var ttkEl)
                                    && ttkEl.ValueKind == JsonValueKind.Number
                                    && ttkEl.TryGetDouble(out var ttkSec)
                                    && ttkSec > 0
                                    && ttkSec < 7200)
                                {
                                    ttkSumB[bi] += ttkSec;
                                    ttkCountB[bi]++;
                                }
                            }
                        }
                    }
                }

                if (lastRoomVisitsInFile is not null)
                {
                    foreach (var kv in lastRoomVisitsInFile)
                        mergedReplayRoomVisits[kv.Key] = mergedReplayRoomVisits.GetValueOrDefault(kv.Key) + kv.Value;
                }
            }
            catch
            {
                // ignore unreadable file
            }
        }

        session.RoomVisitsByType.Clear();
        foreach (var kv in mergedReplayRoomVisits)
            session.RoomVisitsByType[kv.Key] = kv.Value;

        while (hands.Count > 512)
            hands.RemoveAt(0);

        var charts = BuildReplayCharts(
            eB, hB, pB, dB, dmgInB, dmgOutB, dmgUnkB, blkB, eGainB, eLoseB, eStepB, eTurnB, discB, exhB, genB, killsB, ttkSumB, ttkCountB,
            stPlB, stEnB, stUnkB);
        charts.AddRange(BuildReplayHandDamageCharts(hands));
        return (session, byAct, byCombat, hands, charts);
    }

    private static bool TryParseEnvelopeUtc(JsonElement root, out DateTime utc)
    {
        utc = default;
        if (!root.TryGetProperty("utc", out var u))
            return false;
        try
        {
            if (u.ValueKind != JsonValueKind.String)
                return false;
            var s = u.GetString();
            if (string.IsNullOrEmpty(s))
                return false;
            utc = DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (utc.Kind == DateTimeKind.Unspecified)
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<MetricTimeSeriesChart> BuildReplayCharts(
        long[] eB,
        long[] hB,
        long[] pB,
        long[] dB,
        double[] dmgInB,
        double[] dmgOutB,
        double[] dmgUnkB,
        double[] blkB,
        double[] eGainB,
        double[] eLoseB,
        double[] eStepB,
        long[] eTurnB,
        long[] discB,
        long[] exhB,
        long[] genB,
        long[] killsB,
        double[] ttkSumB,
        long[] ttkCountB,
        double[] stPlB,
        double[] stEnB,
        double[] stUnkB)
    {
        var last = -1;
        for (var i = 63; i >= 0; i--)
        {
            if (eB[i] != 0 || hB[i] != 0 || pB[i] != 0 || dB[i] != 0
                || dmgInB[i] != 0 || dmgOutB[i] != 0 || dmgUnkB[i] != 0 || blkB[i] != 0
                || eGainB[i] != 0 || eLoseB[i] != 0 || eStepB[i] != 0 || eTurnB[i] != 0
                || discB[i] != 0 || exhB[i] != 0 || genB[i] != 0
                || killsB[i] != 0 || ttkSumB[i] != 0
                || stPlB[i] != 0 || stEnB[i] != 0 || stUnkB[i] != 0)
            {
                last = i;
                break;
            }
        }

        if (last < 0)
            return new List<MetricTimeSeriesChart>();
        var n = last + 1;
        const string replayHover =
            "Replay: each point is one 5‑minute wall-clock bucket of NDJSON lines in the selection. Hover shows that bucket’s total.";
        const string replayDerivedHover =
            "Replay: ratios computed inside each 5‑minute bucket (not cumulative run totals).";
        double[] SliceL(long[] src)
        {
            var a = new double[n];
            for (var i = 0; i < n; i++)
                a[i] = src[i];
            return a;
        }

        double[] SliceD(double[] src)
        {
            var a = new double[n];
            for (var i = 0; i < n; i++)
                a[i] = src[i];
            return a;
        }

        var wallCharts = new List<MetricTimeSeriesChart>
        {
            new MetricTimeSeriesChart(
                "Recording rate (replay, per 5 min)",
                [
                    new MetricTimeSeries("NDJSON lines", new Color(0.52f, 0.78f, 1f), SliceL(eB)),
                    new MetricTimeSeries("History lines", new Color(0.45f, 0.88f, 0.58f), SliceL(hB)),
                    new MetricTimeSeries("Plays", new Color(0.32f, 0.9f, 0.52f), SliceL(pB)),
                    new MetricTimeSeries("Draws", new Color(0.42f, 0.68f, 1f), SliceL(dB)),
                ],
                replayHover),
            new MetricTimeSeriesChart(
                "Dmg in / out / block (replay, per 5 min)",
                BuildReplayDamageWallSeries(n, dmgInB, dmgOutB, dmgUnkB, blkB),
                replayHover + DamageChartFilterFootnote()),
            new MetricTimeSeriesChart(
                "Energy (replay, per 5 min)",
                [
                    new MetricTimeSeries("Player energy turns (count)", new Color(1f, 0.92f, 0.45f), SliceL(eTurnB)),
                    new MetricTimeSeries("Energy + (Σ)", new Color(0.95f, 0.82f, 0.35f), SliceD(eGainB)),
                    new MetricTimeSeries("Energy − (Σ)", new Color(0.9f, 0.5f, 0.35f), SliceD(eLoseB)),
                    new MetricTimeSeries("Steps (Σ)", new Color(0.5f, 0.92f, 0.72f), SliceD(eStepB)),
                ],
                replayHover),
            new MetricTimeSeriesChart(
                "Card pile events (replay, per 5 min)",
                [
                    new MetricTimeSeries("Draws", new Color(0.42f, 0.68f, 1f), SliceL(dB)),
                    new MetricTimeSeries("Discards", new Color(1f, 0.72f, 0.28f), SliceL(discB)),
                    new MetricTimeSeries("Exhaust", new Color(1f, 0.42f, 0.38f), SliceL(exhB)),
                    new MetricTimeSeries("Generated", new Color(0.82f, 0.5f, 1f), SliceL(genB)),
                ],
                replayHover),
            new MetricTimeSeriesChart(
                "Powers & afflictions (replay, per 5 min)",
                [
                    new MetricTimeSeries("Lines → player (inferred)", new Color(1f, 0.55f, 0.42f), SliceD(stPlB)),
                    new MetricTimeSeries("Lines → enemies (inferred)", new Color(0.45f, 0.88f, 0.55f), SliceD(stEnB)),
                    new MetricTimeSeries("Lines unknown recipient", new Color(0.72f, 0.72f, 0.78f), SliceD(stUnkB)),
                ],
                replayHover + " From combat_history_power_received and combat_history_card_afflicted; recipient = same string heuristics as live metrics."),
        };

        var playsPerEnergy = new double[n];
        var dmgPerPlay = new double[n];
        var netPile = new double[n];
        var blockPerDmg = new double[n];
        var avgTtk = new double[n];
        for (var i = 0; i < n; i++)
        {
            var et = Math.Max(1, eTurnB[i]);
            playsPerEnergy[i] = (double)pB[i] / et;
            var pl = Math.Max(1, pB[i]);
            var dmgTot = dmgInB[i] + dmgOutB[i] + dmgUnkB[i];
            dmgPerPlay[i] = dmgTot / pl;
            netPile[i] = dB[i] - (double)discB[i] - exhB[i];
            blockPerDmg[i] = dmgTot > 1e-9 ? blkB[i] / dmgTot : 0;
            avgTtk[i] = ttkCountB[i] > 0 ? ttkSumB[i] / ttkCountB[i] : 0;
        }

        wallCharts.Add(new MetricTimeSeriesChart(
            "Tempo & conversion (replay, per 5 min)",
            [
                new MetricTimeSeries("Plays / energy turn", new Color(0.45f, 0.95f, 0.55f), playsPerEnergy),
                new MetricTimeSeries("Damage Σ / plays", new Color(1f, 0.5f, 0.4f), dmgPerPlay),
                new MetricTimeSeries("Net draws−disc−exh", new Color(0.55f, 0.7f, 1f), netPile),
                new MetricTimeSeries("Block Σ / damage Σ", ChartColorBlockRatio, blockPerDmg),
            ],
            replayDerivedHover));
        wallCharts.Add(new MetricTimeSeriesChart(
            "Kills & TTK (replay, per 5 min)",
            [
                new MetricTimeSeries("Enemy defeats (count)", new Color(1f, 0.55f, 0.45f), SliceL(killsB)),
                new MetricTimeSeries("Mean TTK (s, in-bucket)", new Color(0.92f, 0.78f, 0.35f), avgTtk),
            ],
            replayHover + " Mean TTK averages timeToKillSeconds for kills that logged a TTK in that bucket."));
        return wallCharts;
    }

    private static IReadOnlyList<MetricTimeSeriesChart> BuildReplayHandDamageCharts(IReadOnlyList<HandRollup> hands)
    {
        const int maxPts = 72;
        const string handHover =
            "Replay: each X = one player energy hand (combat_player_energy_turn). Damage Σ is parsed combat_history_damage_received lines since the previous such hand in file order.";
        if (hands.Count == 0)
            return Array.Empty<MetricTimeSeriesChart>();
        var slice = hands.Count <= maxPts ? hands : hands.TakeLast(maxPts).ToList();
        var dmg = slice.Select(h => (double)h.DamageInHand).ToArray();
        if (dmg.Length < 1)
            return Array.Empty<MetricTimeSeriesChart>();
        var roll2 = RollingHandSum(dmg, 2);
        var roll3 = RollingHandSum(dmg, 3);
        var steps = slice.Select(h => (double)h.Steps).ToArray();
        var gain = slice.Select(h => (double)h.Gain).ToArray();
        var lose = slice.Select(h => (double)h.Lose).ToArray();
        return
        [
            new MetricTimeSeriesChart(
                "Damage by hand (replay, recent)",
                [new MetricTimeSeries("Damage (Σ / hand)", new Color(1f, 0.45f, 0.38f), dmg)],
                handHover),
            new MetricTimeSeriesChart(
                "Energy motion by hand (replay, recent)",
                [
                    new MetricTimeSeries("Steps / hand", new Color(0.55f, 0.92f, 0.72f), steps),
                    new MetricTimeSeries("Energy + (hand)", new Color(0.95f, 0.82f, 0.35f), gain),
                    new MetricTimeSeries("Energy − (hand)", new Color(0.9f, 0.5f, 0.35f), lose),
                ],
                handHover),
            new MetricTimeSeriesChart(
                "Damage rolling windows (replay, recent)",
                [
                    new MetricTimeSeries("Last 2 hands Σ", new Color(1f, 0.62f, 0.42f), roll2),
                    new MetricTimeSeries("Last 3 hands Σ", new Color(0.95f, 0.5f, 0.55f), roll3),
                ],
                handHover + " Rolling lines sum the current hand and the previous N−1 hands at each point."),
        ];
    }

    private static IReadOnlyList<MetricTimeSeriesChart> BuildReplayHandDamageChartsForCombat(IReadOnlyList<HandRollup> hands, int combatOrdinal)
    {
        var filtered = new List<HandRollup>();
        foreach (var h in hands)
        {
            if (h.CombatOrdinal == combatOrdinal)
                filtered.Add(h);
        }

        var built = BuildReplayHandDamageCharts(filtered);
        if (built.Count > 0)
            return built;
        const string hover =
            "Replay: energy-turn rows from combat_player_energy_turn in the NDJSON selection; filtered to this combat ordinal.";
        return
        [
            new MetricTimeSeriesChart(
                $"Hands in combat #{combatOrdinal} (replay)",
                [new MetricTimeSeries("(no turns in selection)", new Color(0.55f, 0.55f, 0.6f), new double[] { 0 })],
                hover),
        ];
    }

    /// <summary>Strips prior hand charts (session-wide or combat-scoped) before inserting a new combat scope.</summary>
    private static void RemoveHandTimelineCharts(List<MetricTimeSeriesChart> charts, bool replay)
    {
        for (var i = charts.Count - 1; i >= 0; i--)
        {
            var t = charts[i].SectionTitle;
            if (replay ? IsReplayHandTimelineSection(t) : IsLiveHandTimelineSection(t))
                charts.RemoveAt(i);
        }
    }

    private static bool IsLiveHandTimelineSection(string t) =>
        t.StartsWith("Damage by hand (live", StringComparison.Ordinal)
        || t.StartsWith("Energy motion by hand (live", StringComparison.Ordinal)
        || t.StartsWith("Damage rolling windows (live", StringComparison.Ordinal)
        || (t.StartsWith("Hands in combat ", StringComparison.Ordinal) && t.Contains("(live)", StringComparison.Ordinal));

    private static bool IsReplayHandTimelineSection(string t) =>
        t.StartsWith("Damage by hand (replay", StringComparison.Ordinal)
        || t.StartsWith("Energy motion by hand (replay", StringComparison.Ordinal)
        || t.StartsWith("Damage rolling windows (replay", StringComparison.Ordinal)
        || (t.StartsWith("Hands in combat ", StringComparison.Ordinal) && t.Contains("(replay)", StringComparison.Ordinal));

    private static double[] RollingHandSum(double[] perHand, int window)
    {
        var a = new double[perHand.Length];
        for (var i = 0; i < perHand.Length; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < window && i - k >= 0; k++)
                sum += perHand[i - k];
            a[i] = sum;
        }

        return a;
    }

    private static void AppendHistoricalHand(List<HandRollup> list, JsonElement payload, decimal damageInHand)
    {
        TryGetInt(payload, "combatOrdinal", out var cOrd);
        TryGetInt(payload, "handSequence", out var hSeq);
        TryGetDecimalFirst(payload, out var g, "totalGain", "TotalGain", "total_gain");
        TryGetDecimalFirst(payload, out var l, "totalLose", "TotalLose", "total_lose");
        TryGetIntFirst(payload, out var sets, "setOperationCount", "SetOperationCount", "set_operation_count");
        TryGetIntFirst(payload, out var steps, "stepCount", "StepCount", "step_count");
        var pk = TryGetString(payload, "playerKey");
        list.Add(new HandRollup(cOrd, hSeq, pk, steps, g, l, sets, damageInHand));
    }

    private static MetricsVisualModel BuildVisualMerged(
        MetricsDrillView view,
        MetricsAccumulator acc,
        Dictionary<string, MetricsAccumulator> byAct,
        Dictionary<int, MetricsAccumulator> byCombat,
        List<HandRollup> hands,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        string datasetSummary)
    {
        var scope = TelemetryScopeContext.Snapshot();
        const string drillNote =
            "Replay: charts are wall-clock buckets across the whole NDJSON selection (not act/combat-filtered). Hands / multiplayer use session-wide rollups where noted; bars & counters for Act/Combat use buckets when ordinals / act keys exist.";
        var filteredCharts = FilterTimeSeriesChartsForDrill(timeSeriesCharts, view);
        var maxTsPts = filteredCharts.Count == 0
            ? 0
            : filteredCharts.SelectMany(c => c.Series).Max(s => s.Values.Count);
        var preferTs = maxTsPts >= 1;
        return view switch
        {
            MetricsDrillView.Overview => BuildVisualOverviewMerged(scope, acc, filteredCharts, preferTs, datasetSummary),
            MetricsDrillView.Run => BuildVisualRunMerged(acc, filteredCharts, preferTs, datasetSummary),
            MetricsDrillView.Act => BuildVisualActMerged(acc, byAct, filteredCharts, preferTs, datasetSummary, drillNote),
                MetricsDrillView.Combat => BuildVisualCombatMerged(acc, byCombat, hands, filteredCharts, preferTs, datasetSummary, drillNote),
            MetricsDrillView.Hands => BuildVisualHandsMerged(acc, hands, filteredCharts, preferTs, datasetSummary),
            MetricsDrillView.Multiplayer => BuildVisualMultiplayerMerged(scope, acc, filteredCharts, preferTs, datasetSummary, drillNote),
            _ => BuildVisualOverviewMerged(scope, acc, filteredCharts, preferTs, datasetSummary),
        };
    }

    private static MetricsVisualModel BuildVisualOverviewMerged(
        TelemetryScopeSnapshot scope,
        MetricsAccumulator acc,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary)
    {
        var headers = new List<string>
        {
            datasetSummary,
            $"Live snapshot: {scope.RunMode}  ·  act {scope.ActIndex}  ·  combat #{scope.CombatOrdinal}",
        };
        return new MetricsVisualModel
        {
            ViewTitle = "Overview (disk aggregate)",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(acc),
            CardFlowBars = CardFlowBarsAlways(acc),
            RoomVisitBars = RoomVisitBarsFromAccumulator(acc),
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = AccumulatorCounters(acc),
            CardDamageLeaders = BuildCardDamageLeaders(acc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(acc),
            DetailText = FormatOverviewMerged(datasetSummary, acc),
        };
    }

    private static string FormatOverviewMerged(string datasetSummary, MetricsAccumulator acc)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("Overview — aggregated from NDJSON files on disk");
        sb.AppendLine(datasetSummary);
        sb.AppendLine();
        AppendAccumulatorBlock(sb, "Replayed totals", acc);
        return sb.ToString();
    }

    private static MetricsVisualModel BuildVisualRunMerged(
        MetricsAccumulator acc,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary)
    {
        return new MetricsVisualModel
        {
            ViewTitle = "Run (disk aggregate)",
            Headers = new[] { datasetSummary },
            RecordingBars = RecordingVolumeBars(acc),
            CardFlowBars = CardFlowBarsAlways(acc),
            RoomVisitBars = RoomVisitBarsFromAccumulator(acc),
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = AccumulatorCounters(acc),
            CardDamageLeaders = BuildCardDamageLeaders(acc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(acc),
            DetailText = FormatRunMerged(datasetSummary, acc),
        };
    }

    private static string FormatRunMerged(string datasetSummary, MetricsAccumulator acc)
    {
        var sb = new StringBuilder(384);
        sb.AppendLine("Run — totals merged from selected session file(s).");
        sb.AppendLine(datasetSummary);
        sb.AppendLine();
        AppendAccumulatorBlock(sb, "Replayed totals", acc);
        return sb.ToString();
    }

    private static MetricsVisualModel BuildVisualActMerged(
        MetricsAccumulator sessionAcc,
        Dictionary<string, MetricsAccumulator> byAct,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary,
        string drillNote)
    {
        var orderedKeys = byAct.Keys.OrderBy(ParseActKeyOrdinal).ThenBy(k => k, StringComparer.Ordinal).ToList();
        var headers = new List<string>
        {
            datasetSummary,
            drillNote,
            $"{orderedKeys.Count} act bucket(s) in replay.",
        };

        var sel = TelemetryActUiState.SelectedActKey;
        string? useKey = null;
        if (!string.IsNullOrEmpty(sel) && byAct.ContainsKey(sel))
            useKey = sel;
        else if (orderedKeys.Count > 0)
            useKey = orderedKeys[0];

        if (useKey is not null && byAct.TryGetValue(useKey, out var acc))
        {
            headers.Add($"Pinned act: {useKey} (use Act target picker when Scope = Act).");
            var sb = new StringBuilder(384);
            sb.AppendLine(datasetSummary);
            sb.AppendLine($"Act {useKey}:");
            AppendAccumulatorBlock(sb, "Replayed act totals", acc);
            return new MetricsVisualModel
            {
                ViewTitle = $"Act — {useKey} (replay)",
                Headers = headers,
                RecordingBars = RecordingVolumeBars(acc),
                CardFlowBars = CardFlowBarsAlways(acc),
                TimeSeriesCharts = timeSeriesCharts,
                PreferTimeSeriesOverBars = preferTs,
                Counters = AccumulatorCounters(acc),
                CardDamageLeaders = BuildCardDamageLeaders(acc, false),
                StatusEffectLeaders = BuildStatusEffectLeaders(acc),
                DetailText = sb.ToString(),
            };
        }

        headers.Add("No act buckets (need run_context_snapshot / combat_started with act in the log). Session totals:");
        var counters = new List<MetricCounter>();
        foreach (var k in orderedKeys)
        {
            if (!byAct.TryGetValue(k, out var a))
                continue;
            counters.Add(new MetricCounter(k, a.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (counters.Count == 0)
            counters.AddRange(AccumulatorCounters(sessionAcc));
        else
        {
            counters.Insert(
                0,
                new MetricCounter("Session events", sessionAcc.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        return new MetricsVisualModel
        {
            ViewTitle = "Act (disk aggregate)",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(sessionAcc),
            CardFlowBars = CardFlowBarsAlways(sessionAcc),
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = counters,
            CardDamageLeaders = BuildCardDamageLeaders(sessionAcc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(sessionAcc),
            DetailText = FormatRunMerged(datasetSummary, sessionAcc),
        };
    }

    private static MetricsVisualModel BuildVisualCombatMerged(
        MetricsAccumulator sessionAcc,
        Dictionary<int, MetricsAccumulator> byCombat,
        List<HandRollup> hands,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary,
        string drillNote)
    {
        var ordered = byCombat.Keys.Order().ToList();
        var headers = new List<string>
        {
            datasetSummary,
            drillNote,
            $"{ordered.Count} combat bucket(s) in replay.",
            "Hand-by-hand charts below follow the pinned combat; wall-clock / 5m charts stay selection-wide.",
        };

        var sel = TelemetryCombatUiState.SelectedCombatOrdinal;
        int? useOrd = null;
        if (sel is { } pinned && byCombat.ContainsKey(pinned))
            useOrd = pinned;
        else if (ordered.Count > 0)
            useOrd = ordered[0];

        if (useOrd is { } u && byCombat.TryGetValue(u, out var acc))
        {
            headers.Add($"Pinned combat: #{u} (use Combat target when Scope = Combat).");
            var sb = new StringBuilder(384);
            sb.AppendLine(datasetSummary);
            sb.AppendLine($"Combat #{u}:");
            AppendAccumulatorBlock(sb, "Replayed combat totals", acc);
            var combatCharts = timeSeriesCharts.ToList();
            RemoveHandTimelineCharts(combatCharts, replay: true);
            foreach (var c in BuildReplayHandDamageChartsForCombat(hands, u))
                combatCharts.Add(c);
            return new MetricsVisualModel
            {
                ViewTitle = $"Combat #{u} (replay)",
                Headers = headers,
                RecordingBars = RecordingVolumeBars(acc),
                CardFlowBars = CardFlowBarsAlways(acc),
                TimeSeriesCharts = combatCharts,
                PreferTimeSeriesOverBars = preferTs,
                Counters = AccumulatorCounters(acc),
                CardDamageLeaders = BuildCardDamageLeaders(acc, true),
                StatusEffectLeaders = BuildStatusEffectLeaders(acc),
                DetailText = sb.ToString(),
            };
        }

        headers.Add("No combat buckets (need combat_started / combatOrdinal on lines). Session totals:");
        var counters = new List<MetricCounter>();
        foreach (var k in ordered)
        {
            if (!byCombat.TryGetValue(k, out var a))
                continue;
            counters.Add(new MetricCounter($"Combat #{k}", a.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (counters.Count == 0)
            counters.AddRange(AccumulatorCounters(sessionAcc));
        else
        {
            counters.Insert(
                0,
                new MetricCounter("Session events", sessionAcc.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        return new MetricsVisualModel
        {
            ViewTitle = "Combat (disk aggregate)",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(sessionAcc),
            CardFlowBars = CardFlowBarsAlways(sessionAcc),
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = counters,
            CardDamageLeaders = BuildCardDamageLeaders(sessionAcc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(sessionAcc),
            DetailText = FormatRunMerged(datasetSummary, sessionAcc),
        };
    }

    private static MetricsVisualModel BuildVisualHandsMerged(
        MetricsAccumulator acc,
        List<HandRollup> hands,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary)
    {
        var points = hands.Count == 0
            ? Array.Empty<HandBarPoint>()
            : hands.TakeLast(16).Select(h => new HandBarPoint(h.CombatOrdinal, h.HandSequence, h.Steps, h.PlayerKey)).ToArray();

        return new MetricsVisualModel
        {
            ViewTitle = "Hands (disk aggregate)",
            Headers = new[] { datasetSummary, "Last 16 energy turns from replay (oldest → newest in selection)" },
            RecordingBars = RecordingVolumeBars(acc),
            CardFlowBars = CardFlowBarsAlways(acc),
            HandBars = points,
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = AccumulatorCounters(acc),
            CardDamageLeaders = BuildCardDamageLeaders(acc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(acc),
            DetailText = FormatHandsMerged(hands, datasetSummary),
        };
    }

    private static string FormatHandsMerged(List<HandRollup> hands, string datasetSummary)
    {
        var sb = new StringBuilder(640);
        sb.AppendLine("Hands — replayed from disk");
        sb.AppendLine(datasetSummary);
        sb.AppendLine();
        if (hands.Count == 0)
        {
            sb.AppendLine("  (no combat_player_energy_turn lines in selection)");
            return sb.ToString();
        }

        for (var i = hands.Count - 1; i >= 0; i--)
        {
            var h = hands[i];
            var pk = h.PlayerKey ?? "—";
            var dmgNote = h.DamageInHand > 0 ? $"  dmg Σ {h.DamageInHand.ToString(CultureInfo.InvariantCulture)}" : "";
            sb.AppendLine(
                $"  c{h.CombatOrdinal} h{h.HandSequence} {pk}  steps {h.Steps}  +{h.Gain}/-{h.Lose}  set×{h.SetOps}{dmgNote}");
        }

        return sb.ToString();
    }

    private static MetricsVisualModel BuildVisualMultiplayerMerged(
        TelemetryScopeSnapshot scope,
        MetricsAccumulator acc,
        IReadOnlyList<MetricTimeSeriesChart> timeSeriesCharts,
        bool preferTs,
        string datasetSummary,
        string drillNote)
    {
        var headers = new List<string>
        {
            datasetSummary,
            drillNote,
            $"Live party snapshot: {scope.PartyPlayerKeys.Length} key(s)",
        };
        return new MetricsVisualModel
        {
            ViewTitle = "Multiplayer (disk aggregate)",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(acc),
            CardFlowBars = CardFlowBarsAlways(acc),
            TimeSeriesCharts = timeSeriesCharts,
            PreferTimeSeriesOverBars = preferTs,
            Counters = AccumulatorCounters(acc),
            CardDamageLeaders = BuildCardDamageLeaders(acc, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(acc),
            DetailText = FormatRunMerged(datasetSummary, acc),
        };
    }

    private static MetricsVisualModel BuildVisualOverview(
        TelemetryScopeSnapshot scope,
        IReadOnlyList<MetricTimeSeriesChart> liveCharts,
        bool livePrefer)
    {
        var headers = new List<string>
        {
            $"{scope.RunMode}  ·  act {scope.ActIndex} ({ShortAct(scope.ActId)})  ·  map depth {scope.MapDepth}",
            $"party {scope.PartyPlayerKeys.Length}  ·  combat #{scope.CombatOrdinal}  ·  hand seq {scope.HandSequence}",
        };
        if (scope.CombatStartUtc is { } t)
            headers.Add($"combat elapsed ~{(DateTime.UtcNow - t).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s");

        return new MetricsVisualModel
        {
            ViewTitle = "Overview",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            RoomVisitBars = RoomVisitBarsFromAccumulator(Session),
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = AccumulatorCounters(Session),
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatOverview(scope),
        };
    }

    private static MetricsVisualModel BuildVisualRun(
        TelemetryScopeSnapshot scope,
        IReadOnlyList<MetricTimeSeriesChart> liveCharts,
        bool livePrefer)
    {
        return new MetricsVisualModel
        {
            ViewTitle = "Run (session file)",
            Headers = new[] { "One NDJSON file ≈ one game process — merge on disk for multi-session runs." },
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            RoomVisitBars = RoomVisitBarsFromAccumulator(Session),
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = AccumulatorCounters(Session),
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatRun(scope),
        };
    }

    private static MetricsVisualModel BuildVisualAct(
        TelemetryScopeSnapshot scope,
        IReadOnlyList<MetricTimeSeriesChart> liveCharts,
        bool livePrefer)
    {
        var orderedKeys = ListActKeysOrderedLocked();
        var headers = new List<string>
        {
            $"{orderedKeys.Count} act bucket(s) with events.",
            "Live charts sample the whole session; bars & counters use the act bucket when pinned.",
        };
        var sel = TelemetryActUiState.SelectedActKey;
        var liveKey = ActKey(scope);
        if (sel is null)
            headers.Add($"Act target: map act «{liveKey}» (set Act picker to pin another).");
        else
            headers.Add($"Act target: pinned «{sel}»");

        string? resolved = null;
        if (!string.IsNullOrEmpty(sel) && ByAct.TryGetValue(sel, out _))
            resolved = sel;
        else if (!string.IsNullOrEmpty(liveKey) && ByAct.TryGetValue(liveKey, out _))
            resolved = liveKey;

        if (resolved is not null && ByAct.TryGetValue(resolved, out var acc))
        {
            return new MetricsVisualModel
            {
                ViewTitle = $"Act — {resolved}",
                Headers = headers,
                RecordingBars = RecordingVolumeBars(acc),
                CardFlowBars = CardFlowBarsAlways(acc),
                TimeSeriesCharts = liveCharts,
                PreferTimeSeriesOverBars = livePrefer,
                Counters = AccumulatorCounters(acc),
                CardDamageLeaders = BuildCardDamageLeaders(acc, false),
                StatusEffectLeaders = BuildStatusEffectLeaders(acc),
                DetailText = FormatAct(scope, resolved, acc),
            };
        }

        headers.Add("No matching act bucket — table of all acts (NDJSON events per bucket):");
        var counters = new List<MetricCounter>();
        foreach (var k in orderedKeys)
        {
            if (!ByAct.TryGetValue(k, out var a))
                continue;
            counters.Add(new MetricCounter(k, a.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (counters.Count == 0)
            counters.AddRange(AccumulatorCounters(Session));

        return new MetricsVisualModel
        {
            ViewTitle = "Act",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = counters,
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatAct(scope),
        };
    }

    private static MetricsVisualModel BuildVisualCombat(
        TelemetryScopeSnapshot scope,
        IReadOnlyList<MetricTimeSeriesChart> liveCharts,
        bool livePrefer)
    {
        var scopeCombat = scope.CombatOrdinal;
        var pinned = TelemetryCombatUiState.SelectedCombatOrdinal;
        var ordered = ListCombatOrdinalsOrderedLocked();
        var headers = new List<string>
        {
            $"{ordered.Count} combat bucket(s) with events.",
            "Combat scope hides the generic throughput chart. Hand-by-hand lines below match the combat target; Δ dmg / energy / 5m buckets stay session-wide samples.",
        };
        if (pinned is null)
            headers.Add($"Combat target: live fight #{scopeCombat} (0 = not in combat).");
        else
            headers.Add($"Combat target: pinned #{pinned.Value}.");

        var useOrdinal = pinned ?? scopeCombat;
        if (useOrdinal > 0 && ByCombat.TryGetValue(useOrdinal, out var acc))
        {
            var combatCharts = liveCharts.ToList();
            RemoveHandTimelineCharts(combatCharts, replay: false);
            foreach (var c in BuildLiveHandDetailChartsForCombatLocked(useOrdinal))
                combatCharts.Add(c);
            return new MetricsVisualModel
            {
                ViewTitle = $"Combat #{useOrdinal}",
                Headers = headers,
                RecordingBars = RecordingVolumeBars(acc),
                CardFlowBars = CardFlowBarsAlways(acc),
                TimeSeriesCharts = combatCharts,
                PreferTimeSeriesOverBars = livePrefer,
                Counters = AccumulatorCounters(acc),
                CardDamageLeaders = BuildCardDamageLeaders(acc, true),
                StatusEffectLeaders = BuildStatusEffectLeaders(acc),
                DetailText = FormatCombat(scope, useOrdinal, acc),
            };
        }

        headers.Add("No matching combat bucket — per-combat event counts:");
        var counters = new List<MetricCounter>();
        foreach (var k in ordered)
        {
            if (!ByCombat.TryGetValue(k, out var a))
                continue;
            counters.Add(new MetricCounter($"Combat #{k}", a.Events.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (counters.Count == 0)
            counters.AddRange(AccumulatorCounters(Session));

        return new MetricsVisualModel
        {
            ViewTitle = "Combat",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = counters,
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatCombat(scope),
        };
    }

    private static MetricsVisualModel BuildVisualHands(IReadOnlyList<MetricTimeSeriesChart> liveCharts, bool livePrefer)
    {
        var points = HandHistory.Count == 0
            ? Array.Empty<HandBarPoint>()
            : HandHistory.TakeLast(16).Select(h => new HandBarPoint(h.CombatOrdinal, h.HandSequence, h.Steps, h.PlayerKey)).ToArray();

        return new MetricsVisualModel
        {
            ViewTitle = "Hands",
            Headers = new[] { "Column heights = energy steps per turn (last 16, oldest → newest)" },
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            HandBars = points,
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = AccumulatorCounters(Session),
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatHands(),
        };
    }

    private static MetricsVisualModel BuildVisualMultiplayer(
        TelemetryScopeSnapshot scope,
        IReadOnlyList<MetricTimeSeriesChart> liveCharts,
        bool livePrefer)
    {
        var headers = new List<string>
        {
            $"{scope.RunMode}  ·  {scope.PartyPlayerKeys.Length} party key(s) from save snapshot",
        };

        return new MetricsVisualModel
        {
            ViewTitle = "Multiplayer",
            Headers = headers,
            RecordingBars = RecordingVolumeBars(Session),
            CardFlowBars = CardFlowBarsAlways(Session),
            TimeSeriesCharts = liveCharts,
            PreferTimeSeriesOverBars = livePrefer,
            Counters = AccumulatorCountersWithMultiplayerRollups(scope, Session),
            CardDamageLeaders = BuildCardDamageLeaders(Session, false),
            StatusEffectLeaders = BuildStatusEffectLeaders(Session),
            DetailText = FormatMultiplayer(scope),
        };
    }

    /// <summary>Session counters plus per–NetId player rollups for MP coordination (live session only).</summary>
    private static IReadOnlyList<MetricCounter> AccumulatorCountersWithMultiplayerRollups(
        TelemetryScopeSnapshot scope,
        MetricsAccumulator sessionAcc)
    {
        var list = AccumulatorCounters(sessionAcc).ToList();
        AppendMultiplayerPerPlayerCounters(scope, list);
        return list;
    }

    private static void AppendMultiplayerPerPlayerCounters(TelemetryScopeSnapshot scope, List<MetricCounter> list)
    {
        if (ByPlayerSession.Count == 0)
            return;

        list.Add(new MetricCounter(
            "MP: per-player (session)",
            $"{ByPlayerSession.Count} key(s) — dmg / blk / plays"));
        var inParty = new HashSet<string>(scope.PartyPlayerKeys, StringComparer.Ordinal);
        foreach (var pk in scope.PartyPlayerKeys)
        {
            if (!ByPlayerSession.TryGetValue(pk, out var acc))
                continue;
            list.Add(MultiplayerPlayerRollupCounter(pk, acc));
        }

        foreach (var kv in ByPlayerSession
                     .OrderByDescending(k => k.Value.DamageDealtSum)
                     .ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            if (inParty.Contains(kv.Key))
                continue;
            list.Add(MultiplayerPlayerRollupCounter(kv.Key, kv.Value));
        }
    }

    private static MetricCounter MultiplayerPlayerRollupCounter(string playerKey, MetricsAccumulator a) =>
        new(
            $"[{playerKey}]",
            $"dmg {a.DamageDealtSum.ToString("N0", CultureInfo.InvariantCulture)} · " +
            $"blk {a.BlockGainedSum.ToString("N0", CultureInfo.InvariantCulture)} · " +
            $"plays {a.Plays.ToString("N0", CultureInfo.InvariantCulture)}");

    private static IReadOnlyList<MetricBar> RecordingVolumeBars(MetricsAccumulator a)
    {
        return new[]
        {
            new MetricBar("NDJSON events", a.Events, new Color(0.52f, 0.78f, 1f)),
            new MetricBar("Combat history lines", a.CombatHistoryLines, new Color(0.45f, 0.88f, 0.58f)),
            new MetricBar("Run-save lines", a.RunSaveEvents, new Color(0.95f, 0.72f, 0.38f)),
            new MetricBar("run_gold lines", a.GoldUpdates, ChartColorGold),
        };
    }

    /// <summary>Always five categories so the chart is visible even on the main menu (zeros).</summary>
    private static IReadOnlyList<MetricBar> CardFlowBarsAlways(MetricsAccumulator a)
    {
        var items = new (string Label, double Value, Color Color)[]
        {
            ("Plays", a.Plays, new Color(0.32f, 0.9f, 0.52f)),
            ("Draws", a.Draws, new Color(0.42f, 0.68f, 1f)),
            ("Discards", a.Discards, new Color(1f, 0.72f, 0.28f)),
            ("Exhaust", a.Exhausts, new Color(1f, 0.42f, 0.38f)),
            ("Generated", a.Generated, new Color(0.82f, 0.5f, 1f)),
        };

        return items.Select(i => new MetricBar(i.Label, i.Value, i.Color)).ToArray();
    }

    private static IReadOnlyList<MetricBar> RoomVisitBarsFromAccumulator(MetricsAccumulator a)
    {
        if (a.RoomVisitsByType.Count == 0)
            return Array.Empty<MetricBar>();
        return a.RoomVisitsByType
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MetricBar(kv.Key, kv.Value, RoomTypeBarColor(kv.Key)))
            .ToArray();
    }

    private static Color RoomTypeBarColor(string label)
    {
        var h = StringComparer.Ordinal.GetHashCode(label);
        static float Ch(int code, int shift) =>
            0.38f + (float)((code >> shift) & 0x7F) / 127f * 0.55f;
        return new Color(Ch(h, 0), Ch(h, 7), Ch(h, 14), 1f);
    }

    private static IReadOnlyList<MetricCounter> AccumulatorCounters(MetricsAccumulator a)
    {
        var list = new List<MetricCounter>
        {
            new(
                "Gold (run save)",
                a.LastGold is { } gv ? gv.ToString("N0", CultureInfo.InvariantCulture) : "—"),
            new("Rollup: dmg in Σ (player)", a.DamageReceivedToPlayerSum.ToString("N1", CultureInfo.InvariantCulture)),
            new("Rollup: dmg out Σ (enemies)", a.DamageReceivedToEnemySum.ToString("N1", CultureInfo.InvariantCulture)),
            new("Rollup: dmg unclassified Σ", a.DamageReceivedUnknownSum.ToString("N1", CultureInfo.InvariantCulture)),
            new("Rollup: HP lost Σ (all, log)", a.DamageDealtSum.ToString("N1", CultureInfo.InvariantCulture)),
            new("Rollup: block Σ", a.BlockGainedSum.ToString("N1", CultureInfo.InvariantCulture)),
            new("Events", a.Events.ToString("N0", CultureInfo.InvariantCulture)),
            new("History lines", a.CombatHistoryLines.ToString("N0", CultureInfo.InvariantCulture)),
        };

        if (a.PassiveDamageEvents > 0)
        {
            list.Add(new MetricCounter(
                "Rollup: passive dmg lines",
                a.PassiveDamageEvents.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Rollup: passive dmg Σ",
                a.PassiveDamageSum.ToString("N1", CultureInfo.InvariantCulture)));
        }

        if (a.PowerReceivedLines > 0 || a.CardAfflictedLines > 0)
        {
            list.Add(new MetricCounter(
                "Rollup: power_received lines",
                a.PowerReceivedLines.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Rollup: card_afflicted lines",
                a.CardAfflictedLines.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Rollup: status → player (lines)",
                a.StatusEffectOnPlayerEvents.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Rollup: status → enemies (lines)",
                a.StatusEffectOnEnemyEvents.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Rollup: status recipient unknown",
                a.StatusEffectUnknownRecipientEvents.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (a.DamageLines > 0)
            list.Add(new MetricCounter("Damage lines", a.DamageLines.ToString("N0", CultureInfo.InvariantCulture)));

        if (a.BlockLines > 0)
            list.Add(new MetricCounter("Block lines", a.BlockLines.ToString("N0", CultureInfo.InvariantCulture)));

        var netVel = a.Draws - a.Discards - a.Exhausts;
        list.Add(new MetricCounter(
            "Net draws−disc−exh",
            netVel.ToString("N0", CultureInfo.InvariantCulture)));

        if (a.DamageDealtSum > 0)
            list.Add(new MetricCounter(
                "Block Σ / HP lost Σ",
                (a.BlockGainedSum / a.DamageDealtSum).ToString("F2", CultureInfo.InvariantCulture)));

        if (a.Plays > 0)
            list.Add(new MetricCounter(
                "(Disc+exh) / play",
                ((double)(a.Discards + a.Exhausts) / a.Plays).ToString("F2", CultureInfo.InvariantCulture)));

        if (a.EnemyDefeats > 0)
        {
            list.Add(new MetricCounter("Kills", a.EnemyDefeats.ToString(CultureInfo.InvariantCulture)));
            var avg = a.TtkSamples > 0 ? a.TtkSum / a.TtkSamples : (double?)null;
            list.Add(new MetricCounter(
                "Last TTK",
                a.LastTtkSeconds is { } v ? $"{v.ToString("F1", CultureInfo.InvariantCulture)}s" : "—"));
            list.Add(new MetricCounter(
                "Avg TTK",
                avg is { } x ? $"{x.ToString("F1", CultureInfo.InvariantCulture)}s (n={a.TtkSamples})" : "—"));
        }

        if (a.PlayerSegments > 0)
        {
            var avgP = a.PlayerSegmentSecondsSum / a.PlayerSegments;
            list.Add(new MetricCounter("Player turns", a.PlayerSegments.ToString(CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Plays / player turn",
                (a.Plays / (double)Math.Max(1, a.PlayerSegments)).ToString("F2", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter(
                "Dmg in Σ / player turn",
                (a.DamageReceivedToPlayerSum / (decimal)Math.Max(1, a.PlayerSegments)).ToString("F1", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter("Avg turn len", $"{avgP.ToString("F1", CultureInfo.InvariantCulture)}s"));
            list.Add(new MetricCounter(
                "Last turn",
                $"{a.LastPlayerSegmentSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"));
        }

        if (a.EnergyTurns > 0)
        {
            list.Add(new MetricCounter("Energy turns", a.EnergyTurns.ToString(CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter("Energy steps", a.EnergySteps.ToString("N0", CultureInfo.InvariantCulture)));
            list.Add(new MetricCounter("Energy +/−", $"+{a.EnergyGainSum.ToString(CultureInfo.InvariantCulture)}/−{a.EnergyLoseSum.ToString(CultureInfo.InvariantCulture)}"));
            list.Add(new MetricCounter("Set ops", a.EnergySetOps.ToString("N0", CultureInfo.InvariantCulture)));
            var movePerTurn = (a.EnergyGainSum + a.EnergyLoseSum) / a.EnergyTurns;
            list.Add(new MetricCounter(
                "Energy (gain+lose) / turn",
                movePerTurn.ToString("F2", CultureInfo.InvariantCulture)));
            if (TryEnergyEfficiencyDenominator(a, out var eDen, out var eBasis))
            {
                list.Add(new MetricCounter(
                    $"Dmg out / energy ({eBasis})",
                    (a.DamageReceivedToEnemySum / eDen).ToString("F2", CultureInfo.InvariantCulture)));
                list.Add(new MetricCounter(
                    $"Block / energy ({eBasis})",
                    (a.BlockGainedSum / eDen).ToString("F2", CultureInfo.InvariantCulture)));
                list.Add(new MetricCounter(
                    $"Plays / energy ({eBasis})",
                    ((decimal)a.Plays / eDen).ToString("F2", CultureInfo.InvariantCulture)));
            }
        }

        if (a.RunSaveEvents > 0)
            list.Add(new MetricCounter("Run-save events", a.RunSaveEvents.ToString("N0", CultureInfo.InvariantCulture)));

        foreach (var kv in a.RoomVisitsByType.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            list.Add(new MetricCounter(
                $"Room:{kv.Key}",
                kv.Value.ToString("N0", CultureInfo.InvariantCulture)));
        }

        return list;
    }

    /// <summary>Prefer total energy lost (Σ) as divisor for dmg/block/plays per energy; fall back to gain Σ if no loss yet.</summary>
    private static bool TryEnergyEfficiencyDenominator(MetricsAccumulator a, out decimal den, out string basisLabel)
    {
        if (a.EnergyLoseSum > 0)
        {
            den = a.EnergyLoseSum;
            basisLabel = "lose Σ";
            return true;
        }

        if (a.EnergyGainSum > 0)
        {
            den = a.EnergyGainSum;
            basisLabel = "gain Σ";
            return true;
        }

        den = 0;
        basisLabel = "";
        return false;
    }

    private static string FormatOverview(TelemetryScopeSnapshot scope)
    {
        var sb = new StringBuilder(768);
        sb.AppendLine("Overview — scope + session totals");
        sb.AppendLine($"  Run mode: {scope.RunMode}  |  act {scope.ActIndex} ({ShortAct(scope.ActId)})  |  map depth {scope.MapDepth}");
        sb.AppendLine($"  Party: {scope.PartyPlayerKeys.Length}  |  combat # {scope.CombatOrdinal}  |  next hand seq {scope.HandSequence}");
        if (scope.CombatStartUtc is { } t)
            sb.AppendLine($"  Combat elapsed: {(DateTime.UtcNow - t).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s (approx)");
        sb.AppendLine();
        AppendAccumulatorBlock(sb, "Session (this NDJSON file)", Session);
        return sb.ToString();
    }

    private static string FormatRun(TelemetryScopeSnapshot scope)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("Run — same as session file for now (one log per game launch).");
        sb.AppendLine("Use NDJSON on disk to join multiple sessions into one run analytics pipeline.");
        sb.AppendLine();
        AppendAccumulatorBlock(sb, "Session totals", Session);
        return sb.ToString();
    }

    private static string FormatAct(TelemetryScopeSnapshot scope)
    {
        var key = ActKey(scope);
        if (!string.IsNullOrEmpty(key) && ByAct.TryGetValue(key, out var acc))
            return FormatAct(scope, key, acc);
        var sb = new StringBuilder(512);
        sb.AppendLine($"Act — key «{key}»");
        sb.AppendLine("  (no events attributed to this act yet — enter combat or wait for save snapshot.)");
        return sb.ToString();
    }

    private static string FormatAct(TelemetryScopeSnapshot scope, string resolvedKey, MetricsAccumulator acc)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine($"Act — «{resolvedKey}» (map context: {scope.ActIndex}:{ShortAct(scope.ActId)})");
        AppendAccumulatorBlock(sb, "This act", acc);
        return sb.ToString();
    }

    private static string FormatCombat(TelemetryScopeSnapshot scope)
    {
        var c = scope.CombatOrdinal;
        if (c <= 0 || !ByCombat.TryGetValue(c, out var acc))
        {
            var sb0 = new StringBuilder(256);
            sb0.AppendLine($"Combat — ordinal {c} (0 = not in combat since mod loaded / hook)");
            sb0.AppendLine("  (no bucket yet — start a fight.)");
            return sb0.ToString();
        }

        return FormatCombat(scope, c, acc);
    }

    private static string FormatCombat(TelemetryScopeSnapshot scope, int resolvedOrdinal, MetricsAccumulator acc)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine(
            $"Combat — viewing #{resolvedOrdinal}  |  live scope: #{scope.CombatOrdinal}  (0 = not in combat)");
        AppendAccumulatorBlock(sb, $"Combat #{resolvedOrdinal} (group)", acc);
        return sb.ToString();
    }

    private static string FormatHands()
    {
        var sb = new StringBuilder(640);
        sb.AppendLine("Hands — recent player energy-turn rollups (1 row per completed player segment)");
        if (HandHistory.Count == 0)
        {
            sb.AppendLine("  (none yet)");
            return sb.ToString();
        }

        for (var i = HandHistory.Count - 1; i >= 0; i--)
        {
            var h = HandHistory[i];
            var pk = h.PlayerKey ?? "—";
            var dmgNote = h.DamageInHand > 0 ? $"  dmg Σ {h.DamageInHand.ToString(CultureInfo.InvariantCulture)}" : "";
            sb.AppendLine(
                $"  c{h.CombatOrdinal} h{h.HandSequence} {pk}  steps {h.Steps}  +{h.Gain}/-{h.Lose}  set×{h.SetOps}{dmgNote}");
        }

        return sb.ToString();
    }

    private static string FormatMultiplayer(TelemetryScopeSnapshot scope)
    {
        var sb = new StringBuilder(768);
        sb.AppendLine("Multiplayer — group vs individuals (session-to-date)");
        sb.AppendLine($"  Mode: {scope.RunMode}  |  party keys from last save snapshot: {scope.PartyPlayerKeys.Length}");
        sb.AppendLine();
        AppendAccumulatorBlock(sb, "Group (everyone, all combat history)", Session);
        sb.AppendLine();
        if (scope.PartyPlayerKeys.Length == 0)
        {
            sb.AppendLine("Per-player (from NetId on PlayerCmd + history when parsable):");
            foreach (var kv in ByPlayerSession.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  [{kv.Key}]");
                AppendAccumulatorIndented(sb, kv.Value, "    ");
            }

            if (ByPlayerSession.Count == 0)
                sb.AppendLine("  (no per-player lines yet)");
            return sb.ToString();
        }

        foreach (var pk in scope.PartyPlayerKeys)
        {
            sb.AppendLine($"Player {pk}");
            if (!ByPlayerSession.TryGetValue(pk, out var acc))
            {
                sb.AppendLine("  (no attributed events yet this session)");
                continue;
            }

            AppendAccumulatorIndented(sb, acc, "  ");
        }

        sb.AppendLine();
        sb.AppendLine("Other keys seen this session (not in last party snapshot):");
        var known = new HashSet<string>(scope.PartyPlayerKeys, StringComparer.Ordinal);
        var extra = ByPlayerSession.Keys.Where(k => !known.Contains(k)).ToList();
        if (extra.Count == 0)
            sb.AppendLine("  —");
        else
        {
            foreach (var pk in extra.OrderBy(x => x))
            {
                sb.AppendLine($"  [{pk}]");
                AppendAccumulatorIndented(sb, ByPlayerSession[pk], "    ");
            }
        }

        return sb.ToString();
    }

    private static string ActKey(TelemetryScopeSnapshot s)
    {
        if (s.ActIndex < 0)
            return "";
        return $"{s.ActIndex}:{ShortAct(s.ActId)}";
    }

    private static string ShortAct(string actId)
    {
        if (string.IsNullOrEmpty(actId))
            return "?";
        return actId.Length <= 28 ? actId : actId[..25] + "…";
    }

    private static MetricsAccumulator Bucket(Dictionary<string, MetricsAccumulator> d, string key)
    {
        if (!d.TryGetValue(key, out var acc))
        {
            acc = new MetricsAccumulator();
            d[key] = acc;
        }

        return acc;
    }

    private static MetricsAccumulator Bucket(Dictionary<int, MetricsAccumulator> d, int key)
    {
        if (!d.TryGetValue(key, out var acc))
        {
            acc = new MetricsAccumulator();
            d[key] = acc;
        }

        return acc;
    }

    private static void AppendAccumulatorBlock(StringBuilder sb, string title, MetricsAccumulator a)
    {
        sb.AppendLine(title);
        AppendAccumulatorIndented(sb, a, "  ");
    }

    private static void AppendAccumulatorIndented(StringBuilder sb, MetricsAccumulator a, string indent)
    {
        sb.AppendLine($"{indent}events {a.Events:N0}  history {a.CombatHistoryLines:N0}");
        sb.AppendLine(
            $"{indent}plays {a.Plays:N0}  draw {a.Draws:N0}  discard {a.Discards:N0}  exhaust {a.Exhausts:N0}  gen {a.Generated:N0}");
        if (a.DamageLines > 0)
            sb.AppendLine($"{indent}damage lines {a.DamageLines:N0}");
        if (a.BlockLines > 0)
            sb.AppendLine($"{indent}block lines {a.BlockLines:N0}");
        if (a.DamageDealtSum != 0 || a.BlockGainedSum != 0)
        {
            sb.AppendLine(
                $"{indent}HP lost (damage_received): in→player {a.DamageReceivedToPlayerSum.ToString(CultureInfo.InvariantCulture)}  " +
                $"out→enemies {a.DamageReceivedToEnemySum.ToString(CultureInfo.InvariantCulture)}  " +
                $"unclassified {a.DamageReceivedUnknownSum.ToString(CultureInfo.InvariantCulture)}  " +
                $"Σ {a.DamageDealtSum.ToString(CultureInfo.InvariantCulture)}  |  block Σ {a.BlockGainedSum.ToString(CultureInfo.InvariantCulture)}");
        }

        if (a.PassiveDamageEvents > 0)
        {
            sb.AppendLine(
                $"{indent}passive damage (no open card-play): lines {a.PassiveDamageEvents:N0}  Σ {a.PassiveDamageSum.ToString("N1", CultureInfo.InvariantCulture)}");
        }

        if (a.PowerReceivedLines > 0 || a.CardAfflictedLines > 0)
        {
            sb.AppendLine(
                $"{indent}powers received {a.PowerReceivedLines:N0}  card afflictions {a.CardAfflictedLines:N0}  " +
                $"→player {a.StatusEffectOnPlayerEvents:N0}  →enemies {a.StatusEffectOnEnemyEvents:N0}  unknown {a.StatusEffectUnknownRecipientEvents:N0}");
        }

        if (a.DamageByCard.Count > 0)
        {
            var top = a.DamageByCard.OrderByDescending(kv => kv.Value).Take(8).ToList();
            sb.AppendLine($"{indent}damage by source (card play + Passive:…), top {top.Count}:");
            foreach (var kv in top)
                sb.AppendLine($"{indent}  {kv.Key}  {kv.Value.ToString("N1", CultureInfo.InvariantCulture)}");
        }

        if (a.EnemyDefeats > 0)
        {
            var avg = a.TtkSamples > 0 ? a.TtkSum / a.TtkSamples : (double?)null;
            var last = a.LastTtkSeconds is null ? "—" : $"{a.LastTtkSeconds.Value.ToString("F1", CultureInfo.InvariantCulture)}s";
            var avgS = avg is null ? "—" : $"{avg.Value.ToString("F1", CultureInfo.InvariantCulture)}s";
            sb.AppendLine($"{indent}kills {a.EnemyDefeats}  last TTK {last}  avg TTK ({a.TtkSamples}) {avgS}");
        }

        if (a.PlayerSegments > 0)
        {
            var avgP = a.PlayerSegmentSecondsSum / a.PlayerSegments;
            sb.AppendLine(
                $"{indent}player segments {a.PlayerSegments}  total {FmtSec(a.PlayerSegmentSecondsSum)}  avg {FmtSec(avgP)}  last {FmtSec(a.LastPlayerSegmentSeconds)}");
        }

        if (a.EnergyTurns > 0)
        {
            sb.AppendLine(
                $"{indent}energy turns {a.EnergyTurns}  steps {a.EnergySteps:N0}  +{a.EnergyGainSum}/-{a.EnergyLoseSum}  set×{a.EnergySetOps}");
            var movePerTurn = (a.EnergyGainSum + a.EnergyLoseSum) / a.EnergyTurns;
            sb.AppendLine(
                $"{indent}energy (gain+lose)/turn {movePerTurn.ToString("F2", CultureInfo.InvariantCulture)}");
            if (TryEnergyEfficiencyDenominator(a, out var eDen, out var eBasis))
            {
                sb.AppendLine(
                    $"{indent}dmg/energy ({eBasis}) {(a.DamageDealtSum / eDen).ToString("F2", CultureInfo.InvariantCulture)}  " +
                    $"block/energy {(a.BlockGainedSum / eDen).ToString("F2", CultureInfo.InvariantCulture)}  " +
                    $"plays/energy {((decimal)a.Plays / eDen).ToString("F2", CultureInfo.InvariantCulture)}");
            }
        }

        if (a.RunSaveEvents > 0)
            sb.AppendLine($"{indent}run save lines {a.RunSaveEvents:N0}");

        if (a.RoomVisitsByType.Count > 0)
        {
            var parts = a.RoomVisitsByType
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}×{kv.Value}");
            sb.AppendLine($"{indent}visited map rooms: {string.Join("  ", parts)}");
        }
    }

    private static string FmtSec(double s) => $"{s.ToString("F1", CultureInfo.InvariantCulture)}s";

    private static string? TryHistoryPlayerKey(JsonElement payload)
    {
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "NetId", "PlayerNetId", "OwnerNetId", "PlayerId" })
        {
            if (!props.TryGetProperty(key, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s) && ulong.TryParse(s, out var u))
                    return PlayerKeyUtil.FromNetId(u);
            }
            else if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out var u64))
                return PlayerKeyUtil.FromNetId(u64);
        }

        return null;
    }

    private static string? TryGetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return null;
        return p.GetString();
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
            return false;
        return p.TryGetInt32(out value);
    }

    private static bool TryGetDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var p))
            return false;
        if (p.ValueKind == JsonValueKind.Number)
        {
            try
            {
                value = p.GetDecimal();
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            return !string.IsNullOrWhiteSpace(s)
                && decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static bool TryGetDecimalFirst(JsonElement payload, out decimal value, params string[] names)
    {
        foreach (var n in names)
        {
            if (TryGetDecimal(payload, n, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetIntFirst(JsonElement payload, out int value, params string[] names)
    {
        foreach (var n in names)
        {
            if (TryGetInt(payload, n, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private sealed record HandRollup(
        int CombatOrdinal,
        int HandSequence,
        string? PlayerKey,
        int Steps,
        decimal Gain,
        decimal Lose,
        int SetOps,
        decimal DamageInHand);

    private sealed class MetricsAccumulator
    {
        public long Events;
        public long CombatHistoryLines;
        public long Plays;
        public long Draws;
        public long Discards;
        public long Exhausts;
        public long Generated;
        public long DamageLines;
        public long BlockLines;
        /// <summary>Σ parsed amounts from <c>combat_history_damage_received</c> (all victims).</summary>
        public decimal DamageDealtSum;
        /// <summary>Subset of <see cref="DamageDealtSum"/> where heuristics say the victim is the player.</summary>
        public decimal DamageReceivedToPlayerSum;
        /// <summary>Subset where heuristics say the victim is an enemy/creature.</summary>
        public decimal DamageReceivedToEnemySum;
        /// <summary>Subset where victim could not be classified.</summary>
        public decimal DamageReceivedUnknownSum;
        public decimal BlockGainedSum;
        public int EnemyDefeats;
        public double TtkSum;
        public int TtkSamples;
        public double? LastTtkSeconds;
        public int PlayerSegments;
        public double PlayerSegmentSecondsSum;
        public double LastPlayerSegmentSeconds;
        public long RunSaveEvents;
        /// <summary>Latest gold from <c>run_gold</c> / <c>run_context_snapshot</c> when payload includes <c>gold</c>.</summary>
        public int? LastGold;
        /// <summary>Count of gold-changing ingestions (deduped when value unchanged).</summary>
        public long GoldUpdates;
        public int EnergyTurns;
        public long EnergySteps;
        public decimal EnergyGainSum;
        public decimal EnergyLoseSum;
        public int EnergySetOps;
        public readonly Dictionary<string, long> RoomVisitsByType = new(StringComparer.Ordinal);
        /// <summary>Attributed damage: card display names (play-bracket) plus <c>Passive:…</c> buckets.</summary>
        public readonly Dictionary<string, decimal> DamageByCard = new(StringComparer.Ordinal);
        /// <summary>Count of <see cref="AddPassiveAttributedDamage"/> calls (damage lines outside a play bracket).</summary>
        public long PassiveDamageEvents;
        /// <summary>Σ damage attributed to passive buckets (same Σ as keys prefixed <c>Passive:</c> in <see cref="DamageByCard"/>).</summary>
        public decimal PassiveDamageSum;
        public long PowerReceivedLines;
        public long CardAfflictedLines;
        /// <summary><c>PowerReceived</c> / <c>CardAfflicted</c> lines where the inferred recipient is the player (monster debuffs on you, curses on your cards, etc.).</summary>
        public long StatusEffectOnPlayerEvents;
        /// <summary>Lines where the inferred recipient is an enemy (your Vulnerable/Weak on them, etc.).</summary>
        public long StatusEffectOnEnemyEvents;
        public long StatusEffectUnknownRecipientEvents;
        /// <summary>Keys like <c>power:onPlayer:Weak</c> → event count.</summary>
        public readonly Dictionary<string, long> StatusEffectEventsByDirectedKey = new(StringComparer.Ordinal);

        public void Clear()
        {
            Events = CombatHistoryLines = 0;
            Plays = Draws = Discards = Exhausts = Generated = DamageLines = BlockLines = 0;
            DamageDealtSum = DamageReceivedToPlayerSum = DamageReceivedToEnemySum = DamageReceivedUnknownSum = 0;
            BlockGainedSum = 0;
            EnemyDefeats = TtkSamples = PlayerSegments = EnergyTurns = EnergySetOps = 0;
            TtkSum = PlayerSegmentSecondsSum = LastPlayerSegmentSeconds = 0;
            LastTtkSeconds = null;
            EnergySteps = 0;
            EnergyGainSum = EnergyLoseSum = 0;
            RunSaveEvents = 0;
            LastGold = null;
            GoldUpdates = 0;
            PassiveDamageEvents = 0;
            PassiveDamageSum = 0;
            PowerReceivedLines = CardAfflictedLines = 0;
            StatusEffectOnPlayerEvents = StatusEffectOnEnemyEvents = StatusEffectUnknownRecipientEvents = 0;
            RoomVisitsByType.Clear();
            DamageByCard.Clear();
            StatusEffectEventsByDirectedKey.Clear();
        }

        public void AddCardDamage(string cardKey, decimal amt)
        {
            if (amt == 0 || string.IsNullOrWhiteSpace(cardKey))
                return;
            DamageByCard[cardKey] = DamageByCard.GetValueOrDefault(cardKey) + amt;
        }

        /// <param name="kindLabel">Short bucket name (Poison, unlabeled, …); prefixed with Passive: for <see cref="DamageByCard"/>.</param>
        public void AddPassiveAttributedDamage(string kindLabel, decimal amt)
        {
            if (amt <= 0 || string.IsNullOrWhiteSpace(kindLabel))
                return;
            var key = kindLabel.StartsWith("Passive:", StringComparison.Ordinal)
                ? kindLabel.Trim()
                : "Passive:" + kindLabel.Trim();
            PassiveDamageEvents++;
            PassiveDamageSum += amt;
            AddCardDamage(key, amt);
        }

        public void IngestStatusEffectHistory(string eventType, JsonElement payload)
        {
            if (!CombatHistoryStatusEffectMetrics.TryDeriveFromJson(payload, eventType, out var recipient, out var effectKey,
                    out var lineKind))
                return;
            switch (recipient)
            {
                case StatusEffectRecipientKind.ToPlayer:
                    StatusEffectOnPlayerEvents++;
                    break;
                case StatusEffectRecipientKind.ToEnemy:
                    StatusEffectOnEnemyEvents++;
                    break;
                default:
                    StatusEffectUnknownRecipientEvents++;
                    break;
            }

            var dir = recipient switch
            {
                StatusEffectRecipientKind.ToPlayer => "onPlayer",
                StatusEffectRecipientKind.ToEnemy => "onEnemy",
                _ => "unktgt",
            };
            var composite = $"{lineKind}:{dir}:{effectKey}";
            StatusEffectEventsByDirectedKey[composite] = StatusEffectEventsByDirectedKey.GetValueOrDefault(composite) + 1;
        }

        public void Add(string eventType, JsonElement payload, bool ingestRunContextRoomVisits = true)
        {
            Events++;
            if (eventType.StartsWith("combat_history_", StringComparison.Ordinal))
            {
                CombatHistoryLines++;
                switch (eventType)
                {
                    case "combat_history_card_play_started":
                        Plays++;
                        break;
                    case "combat_history_card_drawn":
                        Draws++;
                        break;
                    case "combat_history_card_discarded":
                        Discards++;
                        break;
                    case "combat_history_card_exhausted":
                        Exhausts++;
                        break;
                    case "combat_history_card_generated":
                        Generated++;
                        break;
                    case "combat_history_power_received":
                        PowerReceivedLines++;
                        IngestStatusEffectHistory(eventType, payload);
                        break;
                    case "combat_history_card_afflicted":
                        CardAfflictedLines++;
                        IngestStatusEffectHistory(eventType, payload);
                        break;
                    case "combat_history_damage_received":
                        DamageLines++;
                        if (TryParseDamageFromHistoryPayload(payload, out var dam))
                        {
                            DamageDealtSum += dam;
                            switch (ClassifyDamageReceivedVictimKind(payload))
                            {
                                case DamageReceivedVictimKind.ToPlayer:
                                    DamageReceivedToPlayerSum += dam;
                                    break;
                                case DamageReceivedVictimKind.ToEnemy:
                                    DamageReceivedToEnemySum += dam;
                                    break;
                                default:
                                    DamageReceivedUnknownSum += dam;
                                    break;
                            }
                        }

                        break;
                    case "combat_history_block_gained":
                        BlockLines++;
                        if (TryParseBlockFromHistoryPayload(payload, out var blk))
                            BlockGainedSum += blk;
                        break;
                }
            }
            else if (eventType == "combat_turn_segment")
            {
                if (TryGetString(payload, "side", out var side)
                    && string.Equals(side, "Player", StringComparison.Ordinal)
                    && TryGetDouble(payload, "durationSeconds", out var dur))
                {
                    PlayerSegments++;
                    PlayerSegmentSecondsSum += dur;
                    LastPlayerSegmentSeconds = dur;
                }
            }
            else if (eventType == "combat_enemy_defeated")
            {
                EnemyDefeats++;
                if (TryGetNullableDouble(payload, "timeToKillSeconds", out var ttk) && ttk is not null)
                {
                    TtkSum += ttk.Value;
                    TtkSamples++;
                    LastTtkSeconds = ttk;
                }
            }
            else if (eventType == "combat_player_energy_turn")
            {
                EnergyTurns++;
                if (TryGetIntFirst(payload, out var st, "stepCount", "StepCount", "step_count"))
                    EnergySteps += st;
                if (TryGetDecimalFirst(payload, out var g, "totalGain", "TotalGain", "total_gain"))
                    EnergyGainSum += g;
                if (TryGetDecimalFirst(payload, out var l, "totalLose", "TotalLose", "total_lose"))
                    EnergyLoseSum += l;
                if (TryGetIntFirst(payload, out var so, "setOperationCount", "SetOperationCount", "set_operation_count"))
                    EnergySetOps += so;
            }
            else if (eventType.StartsWith("run_save_", StringComparison.Ordinal))
                RunSaveEvents++;
            else if (ingestRunContextRoomVisits && eventType == "run_context_snapshot")
            {
                if (TryParseRoomVisitsFromPayload(payload, out var rooms))
                {
                    RoomVisitsByType.Clear();
                    foreach (var kv in rooms)
                        RoomVisitsByType[kv.Key] = kv.Value;
                }
            }

            if (eventType == "run_gold" || eventType == "run_context_snapshot")
                IngestGoldFromPayload(payload);
        }

        private void IngestGoldFromPayload(JsonElement payload)
        {
            int g;
            if (!TryGetInt(payload, "gold", out g))
            {
                if (!TryGetDecimal(payload, "gold", out var dec))
                    return;
                g = (int)decimal.Truncate(dec);
            }

            if (LastGold == g)
                return;
            GoldUpdates++;
            LastGold = g;
        }

        private static bool TryGetInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
                return false;
            return p.TryGetInt32(out value);
        }

        private static bool TryGetDecimal(JsonElement obj, string name, out decimal value)
        {
            value = 0;
            if (!obj.TryGetProperty(name, out var p))
                return false;
            if (p.ValueKind == JsonValueKind.Number)
            {
                try
                {
                    value = p.GetDecimal();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                return !string.IsNullOrWhiteSpace(s)
                    && decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            }

            return false;
        }

        private static bool TryGetString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
                return false;
            value = p.GetString() ?? "";
            return true;
        }

        private static bool TryGetDouble(JsonElement obj, string name, out double value)
        {
            value = 0;
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
                return false;
            return p.TryGetDouble(out value);
        }

        private static bool TryGetNullableDouble(JsonElement obj, string name, out double? value)
        {
            value = null;
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
                return true;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d))
            {
                value = d;
                return true;
            }

            return false;
        }
    }

    private static IReadOnlyList<MetricCounter> BuildStatusEffectLeaders(MetricsAccumulator a)
    {
        if (a.StatusEffectEventsByDirectedKey.Count == 0)
            return Array.Empty<MetricCounter>();

        static string PrettyDirectedKey(string k)
        {
            var parts = k.Split(':');
            if (parts.Length >= 3)
                return $"{parts[0]} → {parts[1]}: {parts[2]}";
            return k;
        }

        return a.StatusEffectEventsByDirectedKey
            .OrderByDescending(kv => kv.Value)
            .Take(14)
            .Select(kv => new MetricCounter(PrettyDirectedKey(kv.Key), kv.Value.ToString("N0", CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static IReadOnlyList<MetricCounter> BuildCardDamageLeaders(MetricsAccumulator a, bool markTopCard)
    {
        if (a.DamageByCard.Count == 0)
            return Array.Empty<MetricCounter>();
        var rows = a.DamageByCard
            .OrderByDescending(kv => kv.Value)
            .Take(12)
            .ToList();
        var list = new List<MetricCounter>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var label = rows[i].Key;
            if (markTopCard && i == 0)
                label = "★ " + label;
            list.Add(new MetricCounter(label, rows[i].Value.ToString("N1", CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private static void TryApplyReplayCardAttribution(
        string eventType,
        JsonElement payload,
        int coLine,
        string? actTag,
        CardPlayReplayStacks stacks,
        MetricsAccumulator session,
        Dictionary<string, MetricsAccumulator> byAct,
        Dictionary<int, MetricsAccumulator> byCombat)
    {
        if (eventType == "combat_ended" && TryGetInt(payload, "combatOrdinal", out var coEnd) && coEnd > 0)
        {
            stacks.ClearCombat(coEnd);
            return;
        }

        if (coLine <= 0)
            return;

        switch (eventType)
        {
            case "combat_history_card_play_started":
                stacks.Push(coLine, TryCardKeyFromHistoryPayload(payload) ?? "?");
                break;
            case "combat_history_card_play_finished":
                stacks.Pop(coLine);
                break;
            case "combat_history_damage_received":
                if (!TryParseDamageFromHistoryPayload(payload, out var amt) || amt <= 0)
                    return;
                if (!stacks.TryPeek(coLine, out var cardKey))
                {
                    var passiveKind = PassiveDamageLabel.BuildFromJsonPayload(payload);
                    session.AddPassiveAttributedDamage(passiveKind, amt);
                    if (!string.IsNullOrEmpty(actTag) && byAct.TryGetValue(actTag, out var accActP))
                        accActP.AddPassiveAttributedDamage(passiveKind, amt);
                    if (byCombat.TryGetValue(coLine, out var accCbP))
                        accCbP.AddPassiveAttributedDamage(passiveKind, amt);
                    return;
                }

                session.AddCardDamage(cardKey, amt);
                if (!string.IsNullOrEmpty(actTag) && byAct.TryGetValue(actTag, out var accAct))
                    accAct.AddCardDamage(cardKey, amt);
                if (byCombat.TryGetValue(coLine, out var accCb))
                    accCb.AddCardDamage(cardKey, amt);
                break;
        }
    }

    private static string? TryCardKeyFromHistoryPayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in CardDamageParsing.CardNamePropertyCandidates)
        {
            if (!props.TryGetProperty(name, out var el))
                continue;
            string? s = null;
            if (el.ValueKind == JsonValueKind.String)
                s = el.GetString();
            else if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
                s = d.ToString(CultureInfo.InvariantCulture);
            else if (el.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                s = el.ToString();
            if (!string.IsNullOrWhiteSpace(s) && !string.Equals(s, "<unreadable>", StringComparison.Ordinal))
                return s.Trim();
        }

        return null;
    }

    private sealed class CardPlayReplayStacks
    {
        private readonly Dictionary<int, List<string>> _byCombat = new();

        internal void Clear() => _byCombat.Clear();

        internal void ClearCombat(int combatOrdinal)
        {
            if (combatOrdinal > 0)
                _byCombat.Remove(combatOrdinal);
        }

        internal void Push(int combatOrdinal, string cardKey)
        {
            if (combatOrdinal <= 0)
                return;
            if (!_byCombat.TryGetValue(combatOrdinal, out var list))
            {
                list = new List<string>(4);
                _byCombat[combatOrdinal] = list;
            }

            list.Add(cardKey);
        }

        internal void Pop(int combatOrdinal)
        {
            if (combatOrdinal <= 0 || !_byCombat.TryGetValue(combatOrdinal, out var list) || list.Count == 0)
                return;
            list.RemoveAt(list.Count - 1);
        }

        internal bool TryPeek(int combatOrdinal, out string cardKey)
        {
            cardKey = "";
            if (combatOrdinal <= 0 || !_byCombat.TryGetValue(combatOrdinal, out var list) || list.Count == 0)
                return false;
            cardKey = list[^1];
            return true;
        }
    }
}
