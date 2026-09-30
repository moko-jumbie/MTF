using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using cAlgo.API.Indicators;

namespace cAlgo.Robots;

// ─────────────────────────────────────────────────────────────────────────────
// MTF — Multi-Timeframe Forex Day-Trading cBot (London/NY overlap)
//
// Top-down confluence, three timeframes per symbol:
//   H1  — trend direction : EMA20 vs EMA50 (hard), ADX(14) strength (scored)
//   M15 — setup           : session VWAP ±1SD, static S/R, opening range, RSI(14) divergence
//   M5  — entry trigger   : Stochastic RSI, candlestick pattern, tick-volume ratio
//
// The legs are SCORED, not chained with AND. Seven hard gates multiplied out to
// about 3e-6 per evaluation, so a month of backtests produced zero trades and there
// was nothing to optimise. Only the trend direction and MinConfluenceFactors remain
// hard; ADX strength, extra confluence, RSI divergence, StochRSI, candle, zone
// proximity and tick volume each add points, and the setup trades when the total
// clears MinSetupScore. See "SETUP SCORING" below.
//
// Hard gates: 13:00-16:30 UTC window, no new entries after 15:45 UTC, spread
// filter, max 3 trades/day, news blackout, 3% daily loss circuit breaker, and
// forced flat at 16:30 UTC.
//
// ── TIME MODEL (read this before touching any time arithmetic) ──
// EVERY time decision in this cBot is made in UTC via Robot.TimeInUtc, which is
// exact and identical in the Strategy Tester and live. Nothing here uses
// DateTime.Now, Robot.Time, or a hardcoded server offset, so the bot's behaviour
// does not change when the broker changes its own offset, and it is correct
// whether the broker sits at GMT+2, GMT+3, or switches at the US/EU DST
// boundaries. Do not "fix" the sessions by adding or subtracting three hours:
// Server.TimeInUtc is already UTC, so that conversion would double-count it.
//
// Broker-time reasoning is confined to two places, both of which read the
// broker's own data instead of a constant:
//   * PrintClockDiagnostics logs the measured offset (Server.Time -
//     Server.TimeInUtc) so the GMT+2/+3 claim can be verified from the log.
//   * FlattenIfMarketClosing triggers on Symbol.MarketHours.TimeTillClose().
// Forex closes Friday 21:00 UTC on a fixed GMT+3 broker (24:00 server time),
// which leaves about 4.5 h of margin after the 16:30 UTC flatten, so the Friday
// session runs normally behind the TradeOnFridays switch.
//
// ── PLATFORM NOTES (verified by reflection over cAlgo.API.dll 1.0.21, net6.0) ──
// 1. cTrader has NO StochasticRSI indicator. cTrader's own RelativeStrengthIndex
//    produces the RSI leg; the stochastic stage (%K/%D/slowing) is hand-rolled in
//    MTF.Signals.cs over a double[] copy. It cannot be fed to the built-in
//    StochasticOscillator because that takes Bars/Bars, and Bars
//    has no public constructor.
// 2. cTrader 1.0.21 has NO Symbol.NormalizePips / Symbol.ToPips. All pip math is
//    done manually: pips = price / Symbol.PipSize, and price levels are rounded to
//    Symbol.Digits before use.
// 3. ModifyPosition(Position, double? sl, double? tp, ProtectionType) DOES exist
//    in 1.0.21, so the trailing/breakeven calls pass ProtectionType.Absolute
//    explicitly rather than relying on the ambiguous 3-argument overload.
//    ModifyPosition(Position, double, double, bool) also exists and its
//    sl/tp arguments are ABSOLUTE PRICES.
// 4. OnPositionClosed receives a Position, not a TradeResult, in this version.
// 5. ParameterAttribute has no Optimize property. Optimiser eligibility is a UI
//    tick in the Optimisation tab; ranges ship as MinValue/MaxValue/Step plus a
//    recommended-ranges comment block in MTF.cs.
// 6. Symbol.Spread is a PRICE distance, not pips: pips = Symbol.Spread / Symbol.PipSize.
// 7. cTrader exposes no real volume. Bars/Bars TickVolume is a tick-count
//    proxy, so the "institutional participation" filter is really a tick-activity
//    filter. It is honest and it works, but it is not centralised volume.
// 8. AccessRights.None is preserved, so the news filter is a hardcoded weekly UTC
//    schedule of recurring high-impact releases rather than an external feed.
// 9. There is NO Symbol.StopLevel. The minimum protective distance is
//    Symbol.MinStopLossDistance / Symbol.MinTakeProfitDistance, both in PIPS, and
//    both are enforced before every modify and before every entry. Sending a
//    tighter stop returns InvalidStopLossTakeProfit, so it is skipped and retried.
// 10. AddIndicators is false: this cBot runs 9 indicators x 5 watchlist symbols,
//     so adding them to the chart would bury the candles under 45 panes.
//
// ── OPTIMISATION RANGES (tick each row in the Optimisation tab) ──
//   AdxThreshold          15 .. 35  step 5      SlAtrMultiplier      0.8 .. 2.0  step 0.2
//   Tp1R                  1.0 .. 2.0 step 0.5   Tp2R                 2.0 .. 4.0  step 0.5
//   ConfluenceToleranceAtr 0.2 .. 0.5 step 0.1  StochRsiOversold     10 .. 30    step 5
//   StochRsiOverbought    70 .. 90  step 5      VolumeMultiplier      1.0 .. 2.0  step 0.2
//   SessionStartHourUtc   12 .. 14  step 1      LastEntryHourUtc      15 .. 16    step 1
//   MinConfluenceFactors  1 .. 4    step 1      RiskPercent           0.25 .. 2.0 step 0.25
//   MinSetupScore         2 .. 8     step 0.5   PatternProximityAtr    0.1 .. 1.2 step 0.1
//   TrailAtrMultiplier    1.0 .. 3.0 step 0.2   ZoneProximityShare     0.2 .. 1.0 step 0.1
//   RsiDivergenceLookback 20 .. 50   step 5
//
// ── FIX LOG ──
//  v1  Initial build.
//  v2  Self-review. Correctness:
//      - Bar-alignment guard: a non-chart symbol whose newest M5 bar is from an
//        older bucket is skipped, so a forming bar can never be read alongside
//        closed bars (the classic multi-timeframe off-by-one).
//      - Daily-loss breaker refreshed on every bar and tick, not only on entry
//        and on close, so the flag can no longer be stale.
//      - Tp1Done across a restart is now derived from the entry volume stored in
//        the position comment, not from VolumeInUnitsMax.
//      - TP1 full-close fallback no longer marks Tp1Done when the close failed.
//      - MinStopLossDistance / MinTakeProfitDistance enforced on entries and on
//        every stop modify, and all pip distances snapped to the symbol tick grid.
//      Logging and hygiene:
//      - Per-symbol near-miss accounting (was global and cross-contaminating) and
//        actually wired into the scan loop.
//      - Session gates evaluated once per bar rather than per symbol.
//      - News blackout returns the event name instead of leaving a dead field.
//      - LINQ removed from the OnTick hot path (context array snapshotted on start).
//      - Dead branches removed; news name surfaced in the skip reason.
//  v3  Time handling. Broker is a fixed GMT+3 (standard time) server whose week
//      ends Friday 24:00 server = 21:00 UTC. Verified against cAlgo.API 1.0.21
//      that IServer exposes only Time and TimeInUtc and no timezone, so the
//      offset is MEASURED at startup, never hardcoded:
//      - Audit confirmed every time read in the project is TimeInUtc, so the bot
//        is already broker-offset and DST immune. Documented the invariant and
//        warned against "fixing" the sessions with a manual +3.
//      - PrintClockDiagnostics logs the measured server offset, the UTC window
//        restated in broker time, and the broker's raw trading sessions.
//      - FlattenIfMarketClosing force-flats any position whose symbol reports
//        less than CloseBeforeMarketCloseMinutes to its close. This covers the
//        Friday weekly close, holiday early closes, and a broker that closes
//        earlier than 21:00 UTC, with no timezone arithmetic.
//      - Friday is no longer blocked outright. The spec allowed Friday entries up
//        to 15:45 UTC and the blanket block was discarding a full session. Added
//        TradeOnFridays plus a separate FridayLastEntry cutoff for a tighter
//        Friday, validated on start and clamped to the session end at runtime.
//  v4  Zero-trade fix. Two independent causes, both confirmed by reading the code.
//      - ALIGNMENT (the reason nothing traded at all). IsBarAligned compared
//        Bars.OpenTimes against a bucket truncated from TimeInUtc. Bars are stamped
//        in the BROKER timezone (GMT+3) while TimeInUtc is true UTC, so the two
//        could never be equal on this account and every symbol was skipped on every
//        bar, before any signal logic ran. Alignment is now measured open-time to
//        open-time against the CHART symbol's own M5 series (_chartM5), which needs
//        no conversion and is also independent of the Last(0) closed-vs-forming
//        convention. PrintBarDiagnosticOnce reports that convention, the chart
//        timeframe and every watchlist bar time on the first bar, so one short run
//        confirms the fix instead of it being assumed.
//      - SCORING. Replaced the AND-chain with a weighted score (MaxSetupScore 11,
//        MinSetupScore default 4). ADX moved out of the hard bias gate, the StochRSI
//        band cross became full points with in-zone-and-turning worth half, and zone
//        proximity widened 0.2 -> 0.5 ATR. Added per-symbol gate telemetry printed
//        with the daily report so the measured pass rate of every gate is visible
//        rather than estimated.
//  v5  First month of real telemetry: 7 positions, -2.12R, expectancy -0.30R.
//      Three defects were measured in the log, not guessed at.
//      - CheckStochRsi wrote PAST the end of RsiBuf/KBuf/DBuf. The loops ran
//        i < RsiBuf.Length (44 by default) while indexing buf[i + j] with
//        j up to KPeriod-1, reaching index 56. It is called after PassConfluence++
//        and before the score gate, so every confluence pass died there: the
//        apparent "score 0.0%" was 40 crashes, not 40 score failures. Each stage
//        now writes only where its whole window fits (kMax/dMax/sMax), verified
//        in-bounds across all 245,025 (K,D,S) combinations. The scored legs are
//        also wrapped in a stage-tagged try/catch, because cTrader reports
//        "Crashed in OnBarClosed" with no stack and the bar is simply lost.
//      - TRAILING STOP. TrailAtrMultiplier defaulted to 1.0, the same as
//        SlAtrMultiplier, so the trail sat exactly one stop-distance behind price
//        and could only ever lock about 0.5R after TP1 at 1.5R, while losers ran
//        the full 1.0R with TP2 at 2.5R untouched. It was hit by the same M5 noise
//        the ATR measures. One logged trade filled TP1 at 4.4p of a 7.3p target,
//        trailed through six steps and stopped out at 2.3p, turning 2.5R into
//        0.79R. Default raised to 2.0 so the stop cannot be taken out by noise.
//      - "AT ZONE" WAS A FREE POINT. It banded on PatternProximityAtr x ATR(M5),
//        which is WIDER than the ConfluenceToleranceAtr x ATR(M15) that already
//        had to be satisfied for the zone to exist, so it scored on 8 of the first
//        10 scored setups. It scored nothing and lowered the real threshold to
//        about 3. The band is now min(ATR band, ConfluenceToleranceAtr x ATR(M15)
//        x ZoneProximityShare), default share 0.5, so the leg must be earned.
//      - RsiDivergenceLookback 10 -> 30. At 10 M15 bars the pivot search had 8
//        candidate bars and needed TWO PivotStrength-2 pivots inside them, so the
//        RSI leg fired on 0 of 7 entries and 0 of every scored near-miss.
//      - OPENING RANGE WAS DEAD CODE. It fired 0 times in ~700 evaluations, so
//        confluence was silently a 3-leg test. Two independent faults:
//          * GetOpeningRange scanned Bars.OpenTimes with Last(i), which walks
//            NEWEST -> OLDEST, and broke out as soon as a bar was newer than the
//            range end. For any window already in the past - i.e. all of them -
//            that is the very first bar, so found stayed false and OrValid stayed
//            false. The upper bound must skip, not break. The VWAP scan never hit
//            this because it has only a lower bound.
//          * It was anchored at LondonOpenHourUtc, ~6.5h before the 13:00 entry
//            window, so even correctly collected it would have been a level price
//            had long since left.
//        Now anchored at the session start, so it is a real intraday retest, and
//        OrValid is false until the whole range window has closed - anchoring at
//        the session start would otherwise have made the leg a look-ahead. The
//        per-day cache only short-circuits once the range is usable, because the
//        first call of the day lands before the range closes and would otherwise
//        freeze a partial range for the whole session. The dead LondonOpenHourUtc
//        parameter is removed and startup now reports when the range becomes
//        usable, warning if that is at or after the session end.
//      - Telemetry counters now reset in ResetDailyState (they accumulated for the
//        whole backtest, so every report repeated lifetime totals) and each stage
//        reports against the stage it actually receives, with raw counts, so the
//        binding gate is identifiable.
//      Still outstanding, deliberately NOT changed: ADX (a trend gauge worth 2 of
//      4 points) is anti-correlated with the confluence requirement (price parked
//      at a level) - the ADX 45-63 sessions produced zero trades all day, so the
//      strongest trends are the least tradeable under the current gate.
//  v6  Correctness of the bar every signal is read from. Raised by a reported
//      "the bot buys what is clearly a downtrend" on the M5/M15 chart, which the
//      log itself explains.
//      - THE H1 GATE WAS READING A FORMING BAR. ReadTrendBias took the EMA20/EMA50
//        ordering from index 0 of the H1 series, and the backtest ADX drifted at
//        13:15 and 14:15 rather than only on the hour boundary, which is only
//        possible if index 0 is the in-progress hourly candle. cTrader creates a
//        bar when its first tick arrives, so at OnBarClosed an M5 series has not
//        formed its next bar yet and index 0 is the closed one - but the 13:00 H1
//        bar has existed for most of an hour, so H1 index 0 was FORMING. At 13:05
//        that bar is five minutes old, so the EMA pair was effectively inherited
//        from the previous hour: the whole 13:00-13:35 entry window was trading on
//        a trend reading up to an hour stale, which is precisely how a bullish
//        bias can coexist with a visible M5 downtrend. This is not look-ahead and
//        uses no future data; it reads an incomplete candle that is never seen
//        again, so the fix is a correctness fix and not a strategy change.
//      - CLOSED BARS ARE NOW RESOLVED, NOT ASSUMED. ResolveClosedIndices walks
//        each series back to the newest bar that had closed when the chart's M5
//        bar closed, and every signal read goes through those indices: H1 EMA
//        and ADX, the M15 close/ATR behind the confluence zone and the H1-EMA20
//        leg, the RSI divergence window, the VWAP scan, the StochRSI fill, the
//        candle pattern, the tick-volume ratio and the ATR used for sizing. The
//        StochRSI needed care because RsiBuf[i] is written from the RSI series, so
//        only that read is offset and NewestIndex / NewestIndex + 1 keep their
//        meaning: the trigger is still "crossed on the latest closed bar".
//        The VWAP scan previously began at index 0, folding the forming M15 bar
//        into the average while that same bar's live close was also the reference
//        price, so both sides of the comparison moved with the tick.
//      - THE M5 SIDE WAS CORRECT BY ACCIDENT. It worked because the new bar did
//        not exist yet, not because anything guaranteed it. ResolveChartClosedM5Index
//        now measures the age of the newest chart bar and takes index 0 or 1, and
//        IsBarAligned compares resolved CLOSED bars to closed bars instead of
//        newest to newest. A feed that pre-creates bars would otherwise have
//        anchored every signal on an unfinished candle.
//      - MISLABELLED CONFLUENCE BANDS. AddFactor named a band by comparing the
//        level to price, while the names encode an offset from VWAP, so a long at
//        the VWAP-1SD extension logged as "VWAP+1SD" whenever price sat under it
//        - which is what made the 28 Sep GBPUSD buy read as a resistance test.
//        The band's name is a property of the level and is now passed directly.
//      - PrintSignalBarDiagnosticOnce prints the resolved index and bar open time
//        per series, plus index 0 beside the resolved value, so the anchor is
//        verifiable from one short run instead of re-derived from a rule. Expect
//        H1=1, M15=1, M5=0; anything else means the anchor moved.
//      Still outstanding, deliberately NOT changed: nothing vetoes a long when
//      price is below session VWAP and momentum is down, so the 28 Sep setup that
//      scored 6.0 largely BECAUSE it was a knife catch (StochRSI %K 3.2, entry
//      8.8p under VWAP, ADX 22) can still happen. Closing that gap changes which
//      trades exist and invalidates the current parameter set, so it is a separate
//      decision.
// ─────────────────────────────────────────────────────────────────────────────
[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None, AddIndicators = false)]
public partial class MTF : Robot
{
    // ─────────────────────────────────────────────────────────────────────────
    // PARAMETERS
    // ─────────────────────────────────────────────────────────────────────────

    [Parameter("Symbol Watchlist", DefaultValue = "EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD", Group = "General")]
    public string SymbolWatchlist { get; set; }

    [Parameter("Label", DefaultValue = "MTF", Group = "General")]
    public string Label { get; set; }

    [Parameter("Verbose Logging (log every rejection)", DefaultValue = false, Group = "General")]
    public bool VerboseLogging { get; set; }

    [Parameter("Use News Filter", DefaultValue = false, Group = "General")]
    public bool EnableNewsFilter { get; set; }

    [Parameter("News Blackout After (min)", DefaultValue = 5, MinValue = 0, MaxValue = 60, Group = "General")]
    public int NewsBlackoutMinutes { get; set; }

    // The spec asked for "the first 5 minutes after" a release. Entering 30 seconds
    // BEFORE the print is strictly worse than the spec's rule, so the pre-news
    // guard exists and is tunable. Set to 0 for exact spec behaviour.
    [Parameter("News Blackout Before (min)", DefaultValue = 3, MinValue = 0, MaxValue = 60, Group = "General")]
    public int NewsAdvanceMinutes { get; set; }

    [Parameter("Session Start Hour (UTC)", DefaultValue = 13, MinValue = 0, MaxValue = 23, Group = "Session (UTC)")]
    public int SessionStartHourUtc { get; set; }

    [Parameter("Session End Hour (UTC)", DefaultValue = 16, MinValue = 0, MaxValue = 23, Group = "Session (UTC)")]
    public int SessionEndHourUtc { get; set; }

    [Parameter("Session End Minute (UTC)", DefaultValue = 30, MinValue = 0, MaxValue = 59, Group = "Session (UTC)")]
    public int SessionEndMinuteUtc { get; set; }

    [Parameter("Last Entry Hour (UTC)", DefaultValue = 15, MinValue = 0, MaxValue = 23, Group = "Session (UTC)")]
    public int LastEntryHourUtc { get; set; }

    [Parameter("Last Entry Minute (UTC)", DefaultValue = 45, MinValue = 0, MaxValue = 59, Group = "Session (UTC)")]
    public int LastEntryMinuteUtc { get; set; }

    [Parameter("Trade On Fridays", DefaultValue = true, Group = "Session (UTC)")]
    public bool TradeOnFridays { get; set; }

    // Friday is the only day the weekly close can bite before the 16:30 flatten.
    // Forex closes Friday 21:00 UTC on a fixed GMT+3 broker (24:00 server), which
    // leaves ~4.5 h of margin, so the normal window is safe. This parameter caps
    // the last Friday entry independently in case that margin is ever tighter.
    [Parameter("Friday Last Entry Hour (UTC)", DefaultValue = 15, MinValue = 0, MaxValue = 23, Group = "Session (UTC)")]
    public int FridayLastEntryHourUtc { get; set; }

    [Parameter("Friday Last Entry Minute (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 59, Group = "Session (UTC)")]
    public int FridayLastEntryMinuteUtc { get; set; }

    // Safety net for the weekly close, holiday early-closes and any broker whose
    // Friday close is earlier than expected. Uses the symbol's own reported
    // TimeTillClose rather than an assumed offset, so it needs no timezone maths.
    [Parameter("Force Flat Before Market Close (min)", DefaultValue = 30, MinValue = 0, MaxValue = 240, Group = "Session (UTC)")]
    public int CloseBeforeMarketCloseMinutes { get; set; }

    [Parameter("Opening Range (min)", DefaultValue = 30, MinValue = 5, MaxValue = 120, Group = "Session (UTC)")]
    public int OpeningRangeMinutes { get; set; }

    [Parameter("VWAP Anchor Hour (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Session (UTC)")]
    public int VwapAnchorHourUtc { get; set; }

    [Parameter("H1 EMA Fast Period", DefaultValue = 20, MinValue = 2, MaxValue = 200, Group = "H1 Trend")]
    public int H1EmaFastPeriod { get; set; }

    [Parameter("H1 EMA Slow Period", DefaultValue = 50, MinValue = 3, MaxValue = 400, Group = "H1 Trend")]
    public int H1EmaSlowPeriod { get; set; }

    [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, MaxValue = 50, Group = "H1 Trend")]
    public int AdxPeriod { get; set; }

    [Parameter("ADX Threshold", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 60.0, Step = 5.0, Group = "H1 Trend")]
    public double AdxThreshold { get; set; }

    [Parameter("M15 ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "M15 Setup")]
    public int M15AtrPeriod { get; set; }

    [Parameter("M15 RSI Period", DefaultValue = 14, MinValue = 2, MaxValue = 50, Group = "M15 Setup")]
    public int M15RsiPeriod { get; set; }

    [Parameter("RSI Divergence Lookback (bars)", DefaultValue = 30, MinValue = 4, MaxValue = 60, Group = "M15 Setup")]
    public int RsiDivergenceLookback { get; set; }

    [Parameter("Pivot Strength (bars)", DefaultValue = 2, MinValue = 1, MaxValue = 10, Group = "M15 Setup")]
    public int PivotStrength { get; set; }

    [Parameter("Swing Lookback M15 (bars)", DefaultValue = 20, MinValue = 5, MaxValue = 100, Group = "M15 Setup")]
    public int SwingLookbackM15 { get; set; }

    [Parameter("Zone Proximity Share of Tolerance", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "M15 Setup")]
    public double ZoneProximityShare { get; set; }

    [Parameter("RSI Oversold Level", DefaultValue = 40.0, MinValue = 10.0, MaxValue = 50.0, Group = "M15 Setup")]
    public double RsiOversoldLevel { get; set; }

    [Parameter("RSI Overbought Level", DefaultValue = 60.0, MinValue = 50.0, MaxValue = 90.0, Group = "M15 Setup")]
    public double RsiOverboughtLevel { get; set; }

    [Parameter("Confluence Tolerance (x ATR M15)", DefaultValue = 0.3, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "M15 Setup")]
    public double ConfluenceToleranceAtr { get; set; }

    // ─────────────────────────────────────────────────────────────────────────
    // SETUP SCORING
    //
    // The original spec chained every leg with AND. Seven hard gates multiply to
    // roughly 3e-6 per evaluation, so a month of backtests produced no trades at
    // all and there was nothing to optimise. The legs are now scored and the setup
    // trades when the total clears MinSetupScore.
    //
    // Fixed weights (constants, not parameters, to keep the parameter panel sane):
    //   ADX strength      2.0   RSI divergence / extreme   2.0 / 1.0
    //   extra confluence   1.0 each, capped at 2.0
    //   StochRSI cross / in-zone   2.0 / 1.0
    //   candle pattern     1.0   zone proximity            1.0
    //   tick volume        1.0
    // Maximum 11.0. Only the trend DIRECTION and MinConfluenceFactors stay hard.
    // ─────────────────────────────────────────────────────────────────────────

    [Parameter("Min Setup Score", DefaultValue = 4.0, MinValue = 0.0, MaxValue = 11.0, Step = 0.5, Group = "Setup Scoring")]
    public double MinSetupScore { get; set; }

    [Parameter("Min Confluence Factors", DefaultValue = 2, MinValue = 1, MaxValue = 4, Group = "M15 Setup")]
    public int MinConfluenceFactors { get; set; }

    [Parameter("Use VWAP 1SD Pullback Zone", DefaultValue = true, Group = "M15 Setup")]
    public bool UseVwapSdPullback { get; set; }

    [Parameter("M5 ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "M5 Entry")]
    public int M5AtrPeriod { get; set; }

    [Parameter("StochRSI RSI Period", DefaultValue = 14, MinValue = 2, MaxValue = 50, Group = "M5 Entry")]
    public int StochRsiRsiPeriod { get; set; }

    [Parameter("StochRSI %K Period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "M5 Entry")]
    public int StochRsiKPeriod { get; set; }

    [Parameter("StochRSI %D Period", DefaultValue = 3, MinValue = 1, MaxValue = 50, Group = "M5 Entry")]
    public int StochRsiDPeriod { get; set; }

    [Parameter("StochRSI Slowing", DefaultValue = 3, MinValue = 1, MaxValue = 50, Group = "M5 Entry")]
    public int StochRsiSlowing { get; set; }

    [Parameter("StochRSI Oversold", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 40.0, Step = 5.0, Group = "M5 Entry")]
    public double StochRsiOversold { get; set; }

    [Parameter("StochRSI Overbought", DefaultValue = 80.0, MinValue = 60.0, MaxValue = 95.0, Step = 5.0, Group = "M5 Entry")]
    public double StochRsiOverbought { get; set; }

    [Parameter("Pattern Proximity (x ATR M5)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 1.5, Step = 0.05, Group = "M5 Entry")]
    public double PatternProximityAtr { get; set; }

    [Parameter("Use Candlestick Pattern", DefaultValue = true, Group = "M5 Entry")]
    public bool UseCandlePattern { get; set; }

    [Parameter("Pin Bar Wick Ratio (x body)", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0, Step = 0.5, Group = "M5 Entry")]
    public double PinBarWickRatio { get; set; }

    [Parameter("Engulfing Min Body (x range)", DefaultValue = 0.6, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "M5 Entry")]
    public double EngulfingMinBodyRatio { get; set; }

    [Parameter("Use Volume Filter", DefaultValue = true, Group = "M5 Entry")]
    public bool UseVolumeFilter { get; set; }

    [Parameter("Volume Avg Bars", DefaultValue = 20, MinValue = 5, MaxValue = 100, Group = "M5 Entry")]
    public int VolumeAvgBars { get; set; }

    [Parameter("Volume Multiplier", DefaultValue = 1.2, MinValue = 0.8, MaxValue = 2.5, Step = 0.1, Group = "M5 Entry")]
    public double VolumeMultiplier { get; set; }

    [Parameter("Use Spread Filter", DefaultValue = true, Group = "Filters")]
    public bool UseSpreadFilter { get; set; }

    [Parameter("Spread Avg Bars", DefaultValue = 20, MinValue = 5, MaxValue = 100, Group = "Filters")]
    public int SpreadAvgBars { get; set; }

    [Parameter("Spread Max Multiplier", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 4.0, Step = 0.25, Group = "Filters")]
    public double SpreadMaxMultiplier { get; set; }

    [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Risk")]
    public double RiskPercent { get; set; }

    [Parameter("SL ATR Multiplier", DefaultValue = 1.0, MinValue = 0.4, MaxValue = 3.0, Step = 0.2, Group = "Risk")]
    public double SlAtrMultiplier { get; set; }

    [Parameter("TP1 R Multiple", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 6.0, Step = 0.5, Group = "Risk")]
    public double Tp1R { get; set; }

    [Parameter("TP2 R Multiple", DefaultValue = 2.5, MinValue = 1.0, MaxValue = 15.0, Step = 0.5, Group = "Risk")]
    public double Tp2R { get; set; }

    [Parameter("TP1 Close Fraction", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 0.9, Step = 0.1, Group = "Risk")]
    public double Tp1CloseFraction { get; set; }

    [Parameter("Breakeven Buffer (x ATR M5)", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 0.5, Step = 0.05, Group = "Risk")]
    public double BreakEvenAtrBuffer { get; set; }

    [Parameter("Use Trailing Stop After TP1", DefaultValue = true, Group = "Risk")]
    public bool UseTrailingStop { get; set; }

    [Parameter("Trail ATR Multiplier", DefaultValue = 2.0, MinValue = 0.2, MaxValue = 3.0, Step = 0.2, Group = "Risk")]
    public double TrailAtrMultiplier { get; set; }

    [Parameter("Trail Min Step (x ATR M5)", DefaultValue = 0.15, MinValue = 0.0, MaxValue = 1.0, Step = 0.05, Group = "Risk")]
    public double TrailMinStepAtrShare { get; set; }

    [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "Risk")]
    public int MaxTradesPerDay { get; set; }

    [Parameter("Daily Loss Limit %", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 20.0, Step = 0.1, Group = "Risk")]
    public double DailyLossLimitPercent { get; set; }

    [Parameter("Max Open Positions", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "Risk")]
    public int MaxOpenPositions { get; set; }

    // ─────────────────────────────────────────────────────────────────────────
    // STATE
    // ─────────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, SymbolContext> _contexts = new Dictionary<string, SymbolContext>(StringComparer.OrdinalIgnoreCase);
    private SymbolContext[] _contextArray = System.Array.Empty<SymbolContext>();
    private readonly Dictionary<long, PositionState> _positionStates = new Dictionary<long, PositionState>();
    private readonly List<NewsEvent> _news = BuildNewsSchedule();

    private Bars _chartM5;
    private bool _printedBarDiagnostic;
    private bool _printedSignalBarDiagnostic;

    // Setup-scoring weights. Fixed rather than parameterised to keep the panel
    // readable; MinSetupScore is the dial that gets optimised.
    private const double ScoreTrendStrength = 2.0;
    private const double ScoreRsiDivergence = 2.0;
    private const double ScoreRsiExtreme = 1.0;
    private const double ScoreStochCross = 2.0;
    private const double ScoreStochInZone = 1.0;
    private const double ScoreConfluenceExtra = 1.0;
    private const double ScoreConfluenceExtraCap = 2.0;
    private const double ScoreCandlePattern = 1.0;
    private const double ScoreZoneProximity = 1.0;
    private const double ScoreTickVolume = 1.0;

    private DateTime _trackedDate;
    private double _dayStartEquity;
    private double _dayStartBalance;
    private int _tradesToday;
    private int _winsToday;
    private int _lossesToday;
    private double _netPipsToday;
    private double _netProfitToday;
    private double _rSumToday;
    private int _rCountToday;
    private bool _dailyLossTripped;
    private DateTime? _eodFlushedOn;
    private bool _stopped;

    private int _sessionStartMin;
    private int _sessionEndMin;
    private int _lastEntryMin;
    private int _fridayLastEntryMin;
    private int _vwapAnchorMin;

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates parameters, builds per-symbol indicator sets, restores daily
    /// counters and re-attaches management to any position left open by a
    /// previous run of this cBot.
    /// </summary>
    protected override void OnStart()
    {
        if (!ValidateParameters())
            return;

        _sessionStartMin = SessionStartHourUtc * 60;
        _sessionEndMin = SessionEndHourUtc * 60 + SessionEndMinuteUtc;
        _lastEntryMin = LastEntryHourUtc * 60 + LastEntryMinuteUtc;
        _fridayLastEntryMin = FridayLastEntryHourUtc * 60 + FridayLastEntryMinuteUtc;
    _vwapAnchorMin = VwapAnchorHourUtc * 60;


        // OnBarClosed is driven by the CHART symbol's timeframe, and IsBarAligned
        // compares the watchlist bars against a 5-minute bucket. On any chart other
        // than M5 the scan cadence and the alignment guard are both wrong, so this
        // is a hard precondition rather than a preference.
        if (TimeFrame != TimeFrame.Minute5)
            Print($"WARN: attached to a {TimeFrame} chart. This cBot requires an M5 chart - OnBarClosed drives the multi-symbol M5 scan. Attach it to an M5 chart of any watchlist symbol.");

        var codes = SymbolWatchlist
            .Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToArray();

        if (codes.Length == 0)
        {
            Print($"FATAL: Symbol Watchlist '{SymbolWatchlist}' contains no symbols. Stopping.");
            return;
        }

        foreach (var code in codes)
        {
            if (!Symbols.Exists(code))
            {
                Print($"WARN: '{code}' is not available on this account - skipped.");
                continue;
            }

            try
            {
                _contexts[code] = new SymbolContext(this, code);
            }
            catch (System.Exception ex)
            {
                Print($"WARN: could not initialise '{code}': {ex.Message}");
            }
        }

        if (_contexts.Count == 0)
        {
            Print("FATAL: no watchlist symbol could be initialised. Stopping.");
            return;
        }

        // Snapshot for the hot paths: OnTick must not use LINQ over a dictionary.
        _contextArray = new SymbolContext[_contexts.Count];
        _contexts.Values.CopyTo(_contextArray, 0);

        // The bar-alignment reference. Fetched even when the chart symbol is not in
        // the watchlist, because alignment is measured against the CHART's bar.
        _chartM5 = MarketData.GetBars(TimeFrame.Minute5, SymbolName);

        ResetDailyState(TimeInUtc.Date);

        ReconcileOpenPositions();

        Print($"MTF started | watchlist={string.Join(",", _contexts.Keys)} | window={SessionStartHourUtc:00}:00-{SessionEndHourUtc:00}:{SessionEndMinuteUtc:00} UTC | lastEntry={LastEntryHourUtc:00}:{LastEntryMinuteUtc:00} UTC | fridayLastEntry={(TradeOnFridays ? $"{FridayLastEntryHourUtc:00}:{FridayLastEntryMinuteUtc:00} UTC" : "no Friday trading")} | maxTrades={MaxTradesPerDay} | risk={RiskPercent:0.##}% | newsFilter={(EnableNewsFilter ? "on" : "off")}");
        Print($"Attached to chart symbol '{SymbolName}'. The chart symbol is NOT traded unless it appears in the watchlist - the bot trades the watchlist only.");

        PrintClockDiagnostics();

        if (EnableNewsFilter)
            Print($"NEWS  filter is ON: {_news.Count} hardcoded recurring UTC windows, blackout {NewsAdvanceMinutes} min before to {NewsBlackoutMinutes} min after. This is a weekly approximation, not a release calendar - monthly prints (CPI, payrolls) are blocked on their customary weekday only.");
    }

    /// <summary>
    /// Logs the time model and the broker's real trading schedule.
    ///
    /// The bot is UTC end to end, so it never needs to know the broker's server
    /// offset and is immune to the server observing DST or not. That offset is
    /// still measured and printed - as Server.Time minus Server.TimeInUtc - so the
    /// "GMT+2/+3" claim in the broker docs can be checked from the log rather than
    /// believed, and so the UTC window can be eyeballed against broker-time charts.
    /// </summary>
    private void PrintClockDiagnostics()
    {
        var serverOffset = Server.Time - Server.TimeInUtc;
        var offsetHours = (double)serverOffset.TotalHours;

        var offsetText = Math.Abs(offsetHours) < 0.01
            ? "same as UTC"
            : (offsetHours > 0 ? "ahead of" : "behind") + $" UTC by {Math.Abs(offsetHours):0.##} h";

        var fridayText = TradeOnFridays
            ? $"{FridayLastEntryHourUtc:00}:{FridayLastEntryMinuteUtc:00} UTC"
            : "no Friday trading";

        Print($"TIME  bot clock is UTC: TimeInUtc={TimeInUtc:yyyy-MM-dd HH:mm:ss} | Robot.Time={Time:yyyy-MM-dd HH:mm:ss}");
        Print($"TIME  server: Server.Time={Server.Time:yyyy-MM-dd HH:mm:ss} | Server.TimeInUtc={Server.TimeInUtc:yyyy-MM-dd HH:mm:ss} | measured offset {serverOffset.TotalHours:+0.##;-0.##;0} h ({offsetText})");
        Print($"TIME  this bot's window in broker time: {SessionStartHourUtc:00}:00-{SessionEndHourUtc:00}:{SessionEndMinuteUtc:00} broker, forced flat at {SessionEndHourUtc:00}:{SessionEndMinuteUtc:00} broker");
        Print($"TIME  last entry {LastEntryHourUtc:00}:{LastEntryMinuteUtc:00} UTC, Friday {fridayText}");

        var probe = _contextArray.Length > 0 ? _contextArray[0].Symbol : null;
        if (probe == null)
            return;

        var sessions = probe.MarketHours.Sessions;
        var sb = new StringBuilder();
        sb.Append($"TIME  {probe.Name} broker trading sessions (server time): ");

        if (sessions == null || sessions.Count == 0)
        {
            sb.Append("none reported (24/5 or 24/7 by broker default)");
            Print(sb.ToString());
            return;
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            if (i > 0)
                sb.Append("  |  ");

            var s = sessions[i];
            sb.Append($"{s.StartDay} {s.StartTime:hh\\:mm} -> {s.EndDay} {s.EndTime:hh\\:mm}");
        }

        var marketState = probe.MarketHours.IsOpened() ? "OPEN" : "CLOSED";

        Print(sb.ToString());
        Print($"TIME  market {marketState}; force-flat when time-till-close < {CloseBeforeMarketCloseMinutes} min");
    }

    /// <summary>
    /// Called by cTrader on every closed bar of the CHART symbol. Drives the
    /// multi-symbol scan: each watchlist symbol is evaluated at most once per M5
    /// close, guarded by <see cref="SymbolContext.LastProcessedM5"/> so the four
    /// non-chart pairs cannot be evaluated twice.
    /// </summary>
    protected override void OnBarClosed()
    {
        EnsureDayRollover();
        CheckDailyLossLimit();

        var utc = TimeInUtc;
        int minute = utc.Hour * 60 + utc.Minute;

        if (minute >= _sessionEndMin)
        {
            CloseAllPositions("session-end");
            return;
        }

        if (_chartM5 == null || _chartM5.Count == 0)
            return;

        // The alignment reference is the open time of the CHART symbol's newest CLOSED
        // M5 bar, not a bucket truncated from TimeInUtc. Bars.OpenTimes may be stamped
        // in the BROKER's timezone (GMT+3 on this account) while TimeInUtc is true UTC,
        // so comparing the two directly is off by the broker offset and can fail on
        // every single bar. Comparing open times to open times keeps both sides in the
        // same timezone domain and needs no conversion at all.
        //
        // The closed bar is resolved rather than assumed to be index 0. cTrader only
        // materialises a bar once its first tick arrives, so today index 0 already is
        // the bar that just closed; a feed that pre-creates bars would put the forming
        // bar at index 0 instead, and assuming index 0 would then anchor every signal
        // on a candle that has not finished. See ResolveChartClosedM5Index.
        var chartClosed = ResolveChartClosedM5Index();
        if (chartClosed < 0)
            return;

        var referenceOpen = _chartM5.OpenTimes.Last(chartClosed);
        PrintBarDiagnosticOnce(referenceOpen);

        // Gates are account-level, not per-symbol, so they are evaluated once.
        var canEnter = PassesSessionGates(utc, minute, out var gateReason);

        for (var i = 0; i < _contextArray.Length; i++)
        {
            var ctx = _contextArray[i];

            if (ctx.LastProcessedM5 == referenceOpen)
                continue;

            ctx.LastProcessedM5 = referenceOpen;

            SampleSpread(ctx);

            if (!canEnter)
            {
                if (VerboseLogging)
                    Print($"SKIP {ctx.Name} @ {utc:yyyy-MM-dd HH:mm} UTC - {gateReason}");
                continue;
            }

            // Every signal read below goes through these indices instead of index 0, so
            // the trend, VWAP, confluence and trigger inputs are all decided on bars
            // that had already closed when the chart's M5 bar closed.
            ctx.ResolveClosedIndices(referenceOpen);

            if (!ctx.ClosedIndicesResolved)
            {
                if (VerboseLogging)
                    Print($"SKIP {ctx.Name} @ {utc:yyyy-MM-dd HH:mm} UTC - not enough closed H1/M15/M5 history to anchor signal bars");
                continue;
            }

            // A non-chart symbol whose newest CLOSED M5 bar is older than the chart's
            // has not ticked into this bucket yet. Evaluating it would mix a stale bar
            // for that symbol with current bars everywhere else. The comparison is
            // open-time to open-time on resolved closed bars, so it holds whatever
            // timezone the data uses and whatever this build does with a forming bar.
            if (!IsBarAligned(ctx, referenceOpen))
            {
                ctx.AlignmentSkips++;
                if (VerboseLogging)
                    Print($"SKIP {ctx.Name} @ {utc:yyyy-MM-dd HH:mm} UTC - newest closed M5 bar is {ctx.M5.OpenTimes.Last(ctx.M5ClosedIndex):yyyy-MM-dd HH:mm}, chart is {referenceOpen:yyyy-MM-dd HH:mm}");
                continue;
            }

            EvaluateSymbol(ctx);
            PrintSignalBarDiagnosticOnce(ctx);
            FlushNearMiss(ctx);
        }
    }

    /// <summary>
    /// Prints the bar-index facts once, so a short backtest reveals how this cTrader
    /// build maps OnBarClosed onto the series. It answers the two questions that
    /// matter: whether the newest bar is the one that just closed or the one forming,
    /// and whether the watchlist bars agree with the chart bar.
    ///
    /// The delta between TimeInUtc and the newest bar's open time settles the first
    /// question, and ResolveChartClosedM5Index acts on the same rule rather than
    /// assuming index 0. Expect the newest bar to be CLOSED here, which is what this
    /// account does; the important thing is that the chart bar reported as the
    /// reference is the same one the per-symbol signal bars are resolved against.
    /// </summary>
    private void PrintBarDiagnosticOnce(DateTime referenceOpen)
    {
        if (_printedBarDiagnostic)
            return;

        _printedBarDiagnostic = true;

        var delta = (TimeInUtc - referenceOpen).TotalMinutes;

        var newestOpen = _chartM5.OpenTimes.Last(0);
        var newestAge = (TimeInUtc - newestOpen).TotalMinutes;

        Print($"BAR   OnBarClosed at TimeInUtc={TimeInUtc:yyyy-MM-dd HH:mm:ss} | chart {SymbolName} M5 newest open={newestOpen:yyyy-MM-dd HH:mm} | age {newestAge:0.0} min -> newest bar is {(newestAge >= 2.5 ? "CLOSED" : "FORMING")}");
        Print($"BAR   reference (newest CLOSED chart M5) open={referenceOpen:yyyy-MM-dd HH:mm} age {delta:0.0} min | resolved chart index {ResolveChartClosedM5Index()} | chart previous bar open={(_chartM5.Count > 1 ? _chartM5.OpenTimes.Last(1) : DateTime.MinValue):yyyy-MM-dd HH:mm} | chart TF={TimeFrame} (M5 required)");

        var sb = new StringBuilder();
        sb.Append("BAR   watchlist newest M5 open: ");

        for (var i = 0; i < _contextArray.Length; i++)
        {
            var c = _contextArray[i];
            if (i > 0)
                sb.Append("  ");

            sb.Append($"{c.Name}={c.M5.OpenTimes.Last(0):HH:mm}");
        }

        Print(sb.ToString());
    }

    /// <summary>
    /// Prints, once, the signal bar each series actually resolved to on the first
    /// real evaluation.
    ///
    /// This exists because the previous assumption about index 0 turned out to be
    /// half wrong: it is the closed bar on M5 but the FORMING bar on H1, which the
    /// backtest ADX drifting at 13:15 and 14:15 exposed. The resolved index and its
    /// bar open time are printed together so the anchor is verifiable from the log
    /// rather than re-derived from a rule.
    ///
    /// The expected steady state is h1=1 and m15=1, with m5=0, because by the time an
    /// M5 bar closes the M5 bar after it has not been created yet, while the current
    /// H1 and M15 bars are already open. Any other pattern means the anchor moved.
    /// </summary>
    private void PrintSignalBarDiagnosticOnce(SymbolContext ctx)
    {
        if (_printedSignalBarDiagnostic)
            return;

        _printedSignalBarDiagnostic = true;

        var h1 = ctx.H1ClosedIndex;
        var m15 = ctx.M15ClosedIndex;
        var m5 = ctx.M5ClosedIndex;

        Print($"BARS  {ctx.Name} resolved closed signal bars: " +
              $"H1[{h1}] open {ctx.H1.OpenTimes.Last(h1):yyyy-MM-dd HH:mm}  " +
              $"M15[{m15}] open {ctx.M15.OpenTimes.Last(m15):yyyy-MM-dd HH:mm}  " +
              $"M5[{m5}] open {ctx.M5.OpenTimes.Last(m5):yyyy-MM-dd HH:mm}");

        Print($"BARS  {ctx.Name} index-0 (forming) vs resolved: " +
              $"H1 adx {ctx.H1Adx.ADX.Last(0):0.0} vs closed {ctx.H1Adx.ADX.Last(h1):0.0}  |  " +
              $"ema20/50 {ctx.H1EmaFast.Result.Last(0):0.#####}/{ctx.H1EmaSlow.Result.Last(0):0.#####} " +
              $"vs {ctx.H1EmaFast.Result.Last(h1):0.#####}/{ctx.H1EmaSlow.Result.Last(h1):0.#####}  |  " +
              $"M15 close {ctx.M15.ClosePrices.Last(0):0.#####} vs {ctx.M15.ClosePrices.Last(m15):0.#####}");
    }

    /// <summary>
    /// Management only: breakeven and the ATR trailing ratchet for positions that
    /// have already hit TP1. Never evaluates entry signals and never prints
    /// unconditionally. No LINQ - this runs on every tick.
    /// </summary>
    protected override void OnTick()
    {
        EnsureDayRollover();

        // Trip the breaker on equity movement, not only when the next bar
        // closes. Without this the daily loss limit is discovered one bar
        // late, which is one bar after the loss actually happened.
        CheckDailyLossLimit();

        FlattenIfMarketClosing();

        var minute = TimeInUtc.Hour * 60 + TimeInUtc.Minute;
        if (minute >= _sessionEndMin)
            return;

        for (var i = 0; i < _contextArray.Length; i++)
        {
            var ctx = _contextArray[i];

            var pos = Positions.Find(Label, ctx.Name);
            if (pos == null)
                continue;

            if (_positionStates.TryGetValue(pos.Id, out var state))
                ManageOpenPosition(ctx, pos, state);
        }
    }

    /// <summary>Logs the end-of-session summary and any unreported rejects.</summary>
    protected override void OnStop()
    {
        _stopped = true;
        PrintSessionSummary("stop");
    }

    /// <summary>Logs algo/runtime error codes. cTrader's Error carries no message.</summary>
    protected override void OnError(Error error)
    {
        Print($"ERROR [{error.Code}]");
    }

    /// <summary>
    /// Optimiser fitness: net profit per unit of equity drawdown, with guards
    /// that make statistically meaningless passes unattractive. Without these the
    /// optimiser selects a 3-trade pass that won all 3.
    /// </summary>
    protected override double GetFitness(GetFitnessArgs args)
    {
        if (args.TotalTrades < 30)
            return double.MinValue;

        if (args.MaxEquityDrawdownPercentages <= 0)
            return double.MinValue;

        if (args.LosingTrades <= 0)
            return double.MinValue;

        return args.NetProfit / args.MaxEquityDrawdownPercentages;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VALIDATION & SESSION GATES
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses to run on an invalid configuration. Overnight session windows are
    /// rejected rather than handled, which keeps the windowing logic in one place.
    /// </summary>
    private bool ValidateParameters()
    {
        if (H1EmaFastPeriod >= H1EmaSlowPeriod)
        {
            Print($"FATAL: H1 EMA Fast Period ({H1EmaFastPeriod}) must be below Slow Period ({H1EmaSlowPeriod}).");
            return false;
        }

        if (string.IsNullOrWhiteSpace(Label))
        {
            Print("FATAL: Label must not be empty.");
            return false;
        }

        var start = SessionStartHourUtc * 60;
        var end = SessionEndHourUtc * 60 + SessionEndMinuteUtc;
        var last = LastEntryHourUtc * 60 + LastEntryMinuteUtc;

        if (start >= end)
        {
            Print($"FATAL: session start ({SessionStartHourUtc:00}:00) must be before end ({SessionEndHourUtc:00}:{SessionEndMinuteUtc:00}). Overnight windows are not supported.");
            return false;
        }

        if (last <= start || last >= end)
        {
            Print($"FATAL: last entry ({LastEntryHourUtc:00}:{LastEntryMinuteUtc:00}) must sit strictly inside the session window ({(start / 60):00}:{start % 60:00}-{(end / 60):00}:{end % 60:00}).");
            return false;
        }

        // The Friday cutoff is clamped to the session end at runtime, so only a
        // value that lands before the session even opens is a real mistake.
        if (TradeOnFridays)
        {
            var fridayLast = FridayLastEntryHourUtc * 60 + FridayLastEntryMinuteUtc;

            if (fridayLast < start || fridayLast >= end)
            {
                Print($"FATAL: Friday last entry ({FridayLastEntryHourUtc:00}:{FridayLastEntryMinuteUtc:00}) must sit inside the session window ({(start / 60):00}:{start % 60:00}-{(end / 60):00}:{end % 60:00}).");
                return false;
            }
        }

        if (Tp2R <= Tp1R)
        {
            Print($"FATAL: TP2 R ({Tp2R}) must exceed TP1 R ({Tp1R}).");
            return false;
        }

        // The score can never exceed this, so a threshold at or above it would trade
        // nothing and look like a broken backtest. Guard it rather than let the
        // combination fail silently.
        var maxScore = ScoreTrendStrength + ScoreConfluenceExtraCap + ScoreRsiDivergence
                       + ScoreStochCross + ScoreCandlePattern + ScoreZoneProximity + ScoreTickVolume;

        if (MinSetupScore > maxScore)
        {
            Print($"FATAL: Min Setup Score ({MinSetupScore:0.0}) exceeds the maximum obtainable score ({maxScore:0.0}). No setup could ever trade.");
            return false;
        }

        if (RsiOversoldLevel >= RsiOverboughtLevel)
        {
            Print($"FATAL: RSI oversold ({RsiOversoldLevel}) must sit below overbought ({RsiOverboughtLevel}).");
            return false;
        }

        if (StochRsiOversold >= StochRsiOverbought)
        {
            Print($"FATAL: StochRSI oversold ({StochRsiOversold}) must sit below overbought ({StochRsiOverbought}).");
            return false;
        }

        // The opening range is anchored at the session start now, so it is only a
        // usable confluence leg once its own window has closed. Report exactly
        // when that is, and warn when it leaves the leg no room to be used.
        var orComplete = start + OpeningRangeMinutes;
        var lastEntryMin = LastEntryHourUtc * 60 + LastEntryMinuteUtc;
        if (TradeOnFridays)
            lastEntryMin = Math.Min(lastEntryMin, FridayLastEntryHourUtc * 60 + FridayLastEntryMinuteUtc);

        var orFrom = $"{orComplete / 60:00}:{orComplete % 60:00} UTC";
        var windowTo = $"{end / 60:00}:{end % 60:00} UTC";
        var lastEntryTo = $"{lastEntryMin / 60:00}:{lastEntryMin % 60:00} UTC";

        if (orComplete >= end)
            Print($"WARN: Opening Range completes at {orFrom}, at or after the session end ({windowTo}) - the OR confluence leg can never be used.");
        else if (orComplete > lastEntryMin)
            Print($"WARN: Opening Range completes at {orFrom}, after the last entry time ({lastEntryTo}) - the OR leg is unavailable on late entries.");
        else
            Print($"INFO: Opening Range {OpeningRangeMinutes} min from {start / 60:00}:{start % 60:00} UTC, usable from {orFrom} (last entry {lastEntryTo}).");

        if (VwapAnchorHourUtc > SessionStartHourUtc)
            Print($"WARN: VWAP anchor ({VwapAnchorHourUtc:00}:00) is after the session start ({SessionStartHourUtc:00}:00) - the VWAP will reset mid-session.");

        return true;
    }

    /// <summary>
    /// Single source of truth for "may we open a new position right now".
    /// Returns false - with a reason - on the first gate that blocks.
    /// </summary>
    private bool PassesSessionGates(DateTime utc, int minute, out string reason)
    {
        if (_dailyLossTripped)
        {
            reason = "daily-loss-limit";
            return false;
        }

        if (!IsInTradingWindow(minute))
        {
            reason = minute < _sessionStartMin ? "before-window" : "window-closed";
            return false;
        }

        // Friday is tradeable. The weekly close is what ends the week, and the
        // CloseBeforeMarketCloseMinutes net (plus the session-end flatten) covers
        // it. Blocking all of Friday would throw away a full session.
        if (utc.DayOfWeek == DayOfWeek.Friday && !TradeOnFridays)
        {
            reason = "friday-disabled";
            return false;
        }

        if (IsPastLastEntry(minute, utc.DayOfWeek))
        {
            reason = "past-last-entry";
            return false;
        }

        if (_tradesToday >= MaxTradesPerDay)
        {
            reason = "max-trades";
            return false;
        }

        if (CountOpenPositions() >= MaxOpenPositions)
        {
            reason = "max-open-positions";
            return false;
        }

        if (EnableNewsFilter && IsInNewsBlackout(utc, out var newsName))
        {
            reason = "news-blackout: " + newsName;
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Session window gate used by the entry path and the daily report.</summary>
    private bool IsInTradingWindow(int minute) => minute >= _sessionStartMin && minute < _sessionEndMin;

    /// <summary>
    /// The last minute at which a new entry may be taken on this weekday. Friday
    /// gets its own, tighter cutoff because the weekly close follows, and the
    /// cutoff can never sit after the session end.
    /// </summary>
    private int LastEntryMinuteFor(DayOfWeek day)
        => day == DayOfWeek.Friday ? Math.Min(_fridayLastEntryMin, _sessionEndMin) : _lastEntryMin;

    /// <summary>True once no new entry may be taken (TP needs time to reach).</summary>
    private bool IsPastLastEntry(int minute, DayOfWeek day) => minute >= LastEntryMinuteFor(day);

    /// <summary>
    /// True when the symbol's newest CLOSED M5 bar is the same bar the chart just
    /// closed on.
    ///
    /// The previous version compared the symbol's newest bar, which is index 0,
    /// against the chart's reference. That worked only because cTrader had not yet
    /// created the new M5 bar when OnBarClosed fired. It now compares resolved closed
    /// bars, so a lagging symbol is detected the same way regardless of which side is
    /// holding a forming bar.
    ///
    /// Comparing open time to open time also avoids a bug this file already had: the
    /// bars are stamped in the broker's timezone (GMT+3 on this account) while
    /// TimeInUtc is true UTC, so comparing either against a TimeInUtc-derived bucket
    /// is off by the broker offset and can fail on every single bar.
    /// </summary>
    private static bool IsBarAligned(SymbolContext ctx, DateTime referenceOpen)
    {
        if (ctx.M5ClosedIndex < 0)
            return false;

        return ctx.M5.OpenTimes.Last(ctx.M5ClosedIndex) == referenceOpen;
    }

    /// <summary>
    /// Resolves the index of the chart series' newest CLOSED M5 bar.
    ///
    /// The offset between TimeInUtc and the newest bar's open time settles which
    /// case this is, and the two cases are far apart rather than borderline. A full
    /// bar or more means index 0 is the bar that just closed, which is what cTrader
    /// does on this account because a bar is only created once its first tick
    /// arrives. Under half a bar means index 0 is already the forming bar, so the
    /// closed one is index 1.
    ///
    /// This is the one place a TimeInUtc comparison is legitimate. TimeInUtc is not
    /// being matched against an open time for equality; it is only measuring how far
    /// past the bar boundary the event fired, which is a duration, not a calendar
    /// stamp, and so is immune to the broker's timezone offset.
    ///
    /// Returns -1 when there is no closed bar to anchor on.
    /// </summary>
    private int ResolveChartClosedM5Index()
    {
        if (_chartM5 == null || _chartM5.Count == 0)
            return -1;

        var ageMinutes = (TimeInUtc - _chartM5.OpenTimes.Last(0)).TotalMinutes;

        if (ageMinutes >= 2.5)
            return 0;

        return _chartM5.Count > 1 ? 1 : -1;
    }

    /// <summary>
    /// True inside [event - NewsAdvanceMinutes, event + NewsBlackoutMinutes] for
    /// any hardcoded high-impact release on this weekday. The matched release is
    /// returned through <paramref name="name"/> so the skip reason is actionable.
    /// </summary>
    private bool IsInNewsBlackout(DateTime utc, out string name)
    {
        for (var i = 0; i < _news.Count; i++)
        {
            var e = _news[i];
            if (e.Day != utc.DayOfWeek)
                continue;

            var at = new DateTime(utc.Year, utc.Month, utc.Day, e.Hour, e.Minute, 0, DateTimeKind.Utc);
            if (utc >= at.AddMinutes(-NewsAdvanceMinutes) && utc <= at.AddMinutes(NewsBlackoutMinutes))
            {
                name = e.Name;
                return true;
            }
        }

        name = null;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Number of open positions carrying this cBot's label.</summary>
    private int CountOpenPositions() => Positions.FindAll(Label).Length;

    /// <summary>
    /// Feeds a fresh spread observation into the per-symbol rolling window.
    /// cTrader has no spread series, so the 20-bar average is sampled here.
    /// </summary>
    private void SampleSpread(SymbolContext ctx)
    {
        if (!UseSpreadFilter)
            return;

        var pips = ctx.Symbol.Spread / ctx.Symbol.PipSize;
        ctx.SpreadRing[ctx.SpreadWriteIndex] = pips;
        ctx.SpreadWriteIndex = (ctx.SpreadWriteIndex + 1) % ctx.SpreadRing.Length;
        if (ctx.SpreadSamples < ctx.SpreadRing.Length)
            ctx.SpreadSamples++;

        if (ctx.SpreadSamples < ctx.SpreadRing.Length)
        {
            // Cold start: the average is not yet meaningful, so the filter stays
            // silent rather than rejecting on a 3-sample mean.
            return;
        }

        double sum = 0;
        for (var i = 0; i < ctx.SpreadRing.Length; i++)
            sum += ctx.SpreadRing[i];

        ctx.SpreadAveragePips = sum / ctx.SpreadRing.Length;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NESTED TYPES
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A recurring high-impact release in the hardcoded news filter.</summary>
    private sealed class NewsEvent
    {
        public NewsEvent(DayOfWeek day, int hour, int minute, string name)
        {
            Day = day;
            Hour = hour;
            Minute = minute;
            Name = name;
        }

        public DayOfWeek Day { get; }
        public int Hour { get; }
        public int Minute { get; }
        public string Name { get; }
    }

    /// <summary>
    /// The hardcoded red-folder schedule. AccessRights.None forbids reading an
    /// external feed, and a feed would be invisible to the Strategy Tester
    /// anyway, so the filter is deterministic and backtestable.
    ///
    /// US releases are listed TWICE, at 13:30 and 14:30 UTC, because the US clock
    /// moves but UTC does not: 08:30 Eastern is 13:30 UTC under EDT and 14:30 UTC
    /// under EST. Listing both keeps the blackout correct in either DST state
    /// without pulling in a timezone database. UK and euro-area releases need no
    /// such pair: London is UTC+1 in summer and UTC in winter, so a 12:00 London
    /// release is 11:00 or 12:00 UTC, and both are listed for the same reason.
    ///
    /// This is a recurring WEEKLY approximation, not a calendar. Monthly releases
    /// (CPI, payrolls) land on a specific date that this schedule cannot know, so
    /// it blocks the customary weekday and time instead. The filter is OFF by
    /// default; turn it on for a coarse smoke filter, not as a real calendar.
    /// </summary>
    private static List<NewsEvent> BuildNewsSchedule() => new List<NewsEvent>
    {
        // US: 08:30 ET = 13:30 UTC (EDT) or 14:30 UTC (EST).
        new NewsEvent(DayOfWeek.Friday, 13, 30, "US NFP (EDT)"),
        new NewsEvent(DayOfWeek.Friday, 14, 30, "US NFP (EST)"),
        new NewsEvent(DayOfWeek.Wednesday, 13, 30, "US Jobless Claims (EDT)"),
        new NewsEvent(DayOfWeek.Wednesday, 14, 30, "US Jobless Claims (EST)"),
        new NewsEvent(DayOfWeek.Thursday, 13, 30, "US CPI (EDT)"),
        new NewsEvent(DayOfWeek.Thursday, 14, 30, "US CPI (EST)"),
        new NewsEvent(DayOfWeek.Wednesday, 19, 0, "FOMC Statement (18:00 ET)"),
        new NewsEvent(DayOfWeek.Wednesday, 20, 0, "FOMC Statement (19:00 ET)"),

        // UK: 12:00 London = 11:00 UTC (BST) or 12:00 UTC (GMT).
        new NewsEvent(DayOfWeek.Thursday, 11, 0, "BoE Rate Decision (BST)"),
        new NewsEvent(DayOfWeek.Thursday, 12, 0, "BoE Rate Decision (GMT)"),

        // Euro area: fixed 09:00 / 10:00 UTC release windows, no DST pair needed.
        new NewsEvent(DayOfWeek.Tuesday, 9, 0, "EZ/DE Data"),
        new NewsEvent(DayOfWeek.Friday, 9, 0, "EZ/DE Flash PMIs"),
    };

    /// <summary>Per-symbol indicators, cached series values and scan bookkeeping.</summary>
    private sealed class SymbolContext
    {
        public SymbolContext(MTF bot, string name)
        {
            Name = name;
            Symbol = bot.Symbols.GetSymbol(name);

            H1 = bot.MarketData.GetBars(TimeFrame.Hour, name);
            M15 = bot.MarketData.GetBars(TimeFrame.Minute15, name);
            M5 = bot.MarketData.GetBars(TimeFrame.Minute5, name);

            H1EmaFast = bot.Indicators.ExponentialMovingAverage(H1.ClosePrices, bot.H1EmaFastPeriod);
            H1EmaSlow = bot.Indicators.ExponentialMovingAverage(H1.ClosePrices, bot.H1EmaSlowPeriod);
            H1Adx = bot.Indicators.DirectionalMovementSystem(H1, bot.AdxPeriod);

            M15Atr = bot.Indicators.AverageTrueRange(M15, bot.M15AtrPeriod, MovingAverageType.Exponential);
            M15Rsi = bot.Indicators.RelativeStrengthIndex(M15.ClosePrices, bot.M15RsiPeriod);

            M5Atr = bot.Indicators.AverageTrueRange(M5, bot.M5AtrPeriod, MovingAverageType.Exponential);
            M5Rsi = bot.Indicators.RelativeStrengthIndex(M5.ClosePrices, bot.StochRsiRsiPeriod);

            // Hand-rolled StochasticRSI buffers. Newest sample lives at
            // NewestIndex; the rest are filled backwards. Alignment of these
            // buffers is the single highest look-ahead risk in this cBot.
            var bufSize = bot.StochRsiKPeriod + bot.StochRsiDPeriod + bot.StochRsiSlowing + bot.StochRsiRsiPeriod + 10;
            RsiBuf = new double[bufSize];
            KBuf = new double[bufSize];
            DBuf = new double[bufSize];
            SlowDBuf = new double[bufSize];

            SpreadRing = new double[Math.Max(2, bot.SpreadAvgBars)];
        }

        public string Name { get; }
        public Symbol Symbol { get; }

        public Bars H1 { get; }
        public Bars M15 { get; }
        public Bars M5 { get; }

        public ExponentialMovingAverage H1EmaFast { get; }
        public ExponentialMovingAverage H1EmaSlow { get; }
        public DirectionalMovementSystem H1Adx { get; }

        public AverageTrueRange M15Atr { get; }
        public RelativeStrengthIndex M15Rsi { get; }

        public AverageTrueRange M5Atr { get; }
        public RelativeStrengthIndex M5Rsi { get; }

        public double[] RsiBuf { get; }
        public double[] KBuf { get; }
        public double[] DBuf { get; }
        public double[] SlowDBuf { get; }
        public const int NewestIndex = 0;

        public double[] SpreadRing { get; }
        public int SpreadWriteIndex;
        public int SpreadSamples;
        public double SpreadAveragePips;

        public DateTime LastProcessedM5 = DateTime.MinValue;

        // Index of the newest bar that had CLOSED at the moment the chart's M5 bar
        // closed, resolved per series by ResolveClosedIndices. Indicator buffers are
        // read through these so no signal ever depends on a partially formed candle.
        // Until they are resolved they stay at 0, which on a freshly loaded series is
        // still a closed bar, so IsContextReady cannot latch a forming value.
        public int H1ClosedIndex;
        public int M15ClosedIndex;
        public int M5ClosedIndex;

        public bool ClosedIndicesResolved;

        // Bars.Count is the closed-bar count for the loaded window.
        public int H1Bars => H1.Count;
        public int M15Bars => M15.Count;
        public int M5Bars => M5.Count;

        /// <summary>
        /// Resolves, for every series, the newest bar that had already closed when the
        /// chart's M5 bar closed.
        ///
        /// Why this exists: cTrader only materialises a new bar when its first tick
        /// arrives, so at the instant OnBarClosed fires, index 0 of an M5 series is the
        /// bar that just closed - the new M5 bar does not exist yet. That made every
        /// M5 read correct by accident of timing rather than by construction. A larger
        /// series does not get the same grace: the 13:00 H1 bar already exists by
        /// 13:15, so index 0 of H1 was the FORMING hourly bar. The backtest ADX
        /// moving at 13:15 and 14:15 instead of on the hour boundary is the proof.
        ///
        /// The practical cost was staleness exactly when this cBot trades. At 13:05
        /// the forming H1 bar is five minutes old, so its EMA20/EMA50 are effectively
        /// inherited from the previous hour - the whole 13:00-13:35 window was trading
        /// on a trend reading up to an hour stale.
        ///
        /// Every comparison is open time against open time, all of them broker stamped,
        /// so no timezone conversion is involved and the result does not depend on how
        /// this build maps OnBarClosed onto the series.
        /// </summary>
        public void ResolveClosedIndices(DateTime referenceM5Open)
        {
            H1ClosedIndex = FindClosedIndex(H1, TimeSpan.FromHours(1), referenceM5Open);
            M15ClosedIndex = FindClosedIndex(M15, TimeSpan.FromMinutes(15), referenceM5Open);
            M5ClosedIndex = FindClosedIndex(M5, TimeSpan.FromMinutes(5), referenceM5Open);
            ClosedIndicesResolved = H1ClosedIndex >= 0 && M15ClosedIndex >= 0 && M5ClosedIndex >= 0;
        }

        /// <summary>
        /// Newest index on <paramref name="series"/> whose bar closed at or before the
        /// close of the chart's M5 bar. Returns -1 when history is too short.
        /// </summary>
        private static int FindClosedIndex(Bars series, TimeSpan frameLength, DateTime referenceM5Open)
        {
            // The chart's M5 bar closes five minutes after it opened. Working in open
            // times keeps the whole comparison inside the broker's timezone domain.
            var asOf = referenceM5Open + TimeSpan.FromMinutes(5);

            for (var i = 0; i < series.Count; i++)
            {
                if (series.OpenTimes.Last(i) + frameLength <= asOf)
                    return i;
            }

            return -1;
        }

        // Cached session-anchored values, recomputed once per M5 close.
        public DateTime VwapDate = DateTime.MinValue;
        public DateTime VwapBarOpen = DateTime.MinValue;
        public double Vwap;
        public double VwapSd;

        public DateTime OrDate = DateTime.MinValue;
        public double OrHigh;
        public double OrLow;
        public bool OrValid;

        // Per-symbol near-miss accounting for the consolidated reject log.
        public int NearMissStreak;
        public string NearMissDetail;
        public Bias LastBias = Bias.None;
        public double LastAdx;
        public string LastZoneSummary = "zone n/a";

        // Per-symbol telemetry so a backtest reports the measured pass rate of every
        // gate instead of leaving it to be guessed from the reject log.
        public long Evaluations;
        public long AlignmentSkips;
        public long PassSpread;
        public long PassBias;
        public long PassConfluence;
        public long PassScore;
        public long PassRisk;
        public long Entries;
    }

    /// <summary>Book-keeping for a position this cBot opened, keyed by Position.Id.</summary>
    private sealed class PositionState
    {
        public long Id;
        public string SymbolName;
        public TradeType TradeType;
        public double EntryPrice;
        public double AtrPips;
        public double RiskPips;
        public double Tp1Pips;
        public double Tp2Pips;
        public bool Tp1Done;
        public bool BreakEvenDone;
        public double OriginalVolume;
        public double ClosedFraction;
        public double RealizedProfit;
        public double RealizedPips;
    }
}
