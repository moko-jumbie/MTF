using System;
using System.Collections.Generic;
using System.Linq;

namespace cAlgo.Robots;

// ─────────────────────────────────────────────────────────────────────────────
// MTF.Signals — the confluence engine.
//
// Every method here runs inside OnBarClosed, and none of them may read a forming
// bar. The bar-index mapping is NOT fixed, which is what this file used to get
// wrong: it asserted that Last(0) is the just-closed bar. That holds for M5,
// because cTrader creates a bar when its first tick arrives and the next M5 bar
// does not exist yet at the instant OnBarClosed fires. It does NOT hold for H1 or
// M15, where the current bar has already existed for most of its length, so index
// 0 there is the bar in progress. That is how the H1 EMA20/50 gate ended up
// reading a trend that was up to an hour stale, and the backtest ADX drifting
// mid-hour is the proof.
//
// The signal bar is therefore RESOLVED per symbol by
// SymbolContext.ResolveClosedIndices and read through ctx.H1ClosedIndex,
// ctx.M15ClosedIndex and ctx.M5ClosedIndex. Loops below walk back from those
// indices. Do not reintroduce a bare Last(0) on a signal read.
// ─────────────────────────────────────────────────────────────────────────────
public partial class MTF
{
    private enum Bias
    {
        None,
        Bullish,
        Bearish
    }

    private enum CandleSignal
    {
        None,
        Bullish,
        Bearish
    }

    private sealed class Confluence
    {
        public bool IsValid;
        public double ZonePrice;
        public int Count;
        public readonly List<string> Factors = new List<string>();
    }

    private sealed class StochRsiState
    {
        public bool IsValid;
        public double K;
        public double D;
        public double SlowD;
        public double PrevSlowD;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WARMUP
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True once every series and indicator has enough closed bars to be
    /// meaningful. Without this the optimiser happily evaluates the first bars
    /// of a run against NaN ADX and zero ATR.
    /// </summary>
    private bool IsContextReady(SymbolContext ctx)
    {
        var required = Math.Max(
            Math.Max(H1EmaSlowPeriod, M15AtrPeriod + RsiDivergenceLookback + PivotStrength + SwingLookbackM15),
            Math.Max(M5AtrPeriod, StochRsiKPeriod + StochRsiDPeriod + StochRsiSlowing + RsiDivergenceLookback));

        if (ctx.H1Bars <= required || ctx.M15Bars <= required || ctx.M5Bars <= required)
            return false;

        var h1 = ctx.H1ClosedIndex;
        var m15 = ctx.M15ClosedIndex;
        var m5 = ctx.M5ClosedIndex;

        if (h1 < 0 || m15 < 0 || m5 < 0)
            return false;

        if (double.IsNaN(ctx.H1EmaFast.Result.Last(h1)) || double.IsNaN(ctx.H1EmaSlow.Result.Last(h1)) || double.IsNaN(ctx.H1Adx.ADX.Last(h1)))
            return false;

        if (ctx.M15Atr.Result.Last(m15) <= 0 || ctx.M5Atr.Result.Last(m5) <= 0)
            return false;

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTF — TREND BIAS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H1 direction from the EMA pair alone: EMA20 above EMA50 is bullish, below is
    /// bearish, equal (or NaN) is no direction. Read at the resolved closed H1 bar so
    /// the gate reflects a completed candle instead of a half-hour-old snapshot that
    /// drifts intrabar - see SymbolContext.ResolveClosedIndices.
    ///
    /// ADX is deliberately NOT part of this. Requiring ADX(14) >= threshold here made
    /// it a hard gate that removed roughly two thirds of all evaluations before any
    /// other logic ran; it is now a scored leg instead. Trend STRENGTH is a
    /// contribution to the setup score, not a veto, while the DIRECTION stays hard
    /// because taking a long against the H1 EMA ordering is a different strategy.
    /// </summary>
    private Bias ReadTrendBias(SymbolContext ctx, out double adxValue)
    {
        var h1 = ctx.H1ClosedIndex;

        adxValue = ctx.H1Adx.ADX.Last(h1);

        var fast = ctx.H1EmaFast.Result.Last(h1);
        var slow = ctx.H1EmaSlow.Result.Last(h1);

        if (double.IsNaN(fast) || double.IsNaN(slow))
            return Bias.None;

        if (fast > slow)
            return Bias.Bullish;

        if (fast < slow)
            return Bias.Bearish;

        return Bias.None;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTF — SESSION VWAP
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Session-anchored VWAP and its standard-deviation band, computed manually
    /// as sum(typical price x volume) / sum(volume) over every M15 bar from the
    /// anchor hour to the just-closed bar.
    ///
    /// cTrader exposes no real volume, so "volume" here is the tick-count proxy
    /// in Bars.TickVolume. That makes this a tick-weighted VWAP: a fair
    /// approximation of the intraday mean, not the institutional VWAP.
    ///
    /// Recomputed whenever a new M15 bar closes, so the session average extends as
    /// the session runs.
    ///
    /// The per-day short-circuit this replaces was wrong twice over. A session VWAP
    /// frozen at the first evaluation of the day is not a session VWAP: with a 13:00
    /// anchor and a 13:00 first evaluation the whole average came from a single
    /// PARTIAL bar, which is why the 28 Sep entry sat 8.8p from a VWAP whose standard
    /// deviation was only 0.00075. It also made that first evaluation load-bearing -
    /// had the closed-bar anchor produced no qualifying bar, the leg would have cached
    /// zero for the entire session.
    ///
    /// Keyed on the resolved closed M15 bar's open time, so this costs one O(session
    /// bars) pass per M15 close per symbol rather than one per M5 bar.
    /// </summary>
    private void CalculateSessionVWAP(SymbolContext ctx, DateTime utc)
    {
        var today = utc.Date;
        var start = ctx.M15ClosedIndex;

        if (start < 0)
            return;

        // Start at the resolved closed M15 bar, not index 0. The forming M15 bar was
        // previously folded into the VWAP while its own live close was also used as
        // the reference price, so both sides of the comparison moved with the tick.
        var currentBar = ctx.M15.OpenTimes.Last(start);

        if (ctx.VwapDate == today && ctx.VwapBarOpen == currentBar)
            return;

        var anchor = today.AddMinutes(_vwapAnchorMin);

        double sumPv = 0, sumV = 0, sumPv2 = 0;
        var bars = 0;

        for (var i = start; i < ctx.M15Bars; i++)
        {
            var openTime = ctx.M15.OpenTimes.Last(i);
            if (openTime < anchor)
                break;

            var high = ctx.M15.HighPrices.Last(i);
            var low = ctx.M15.LowPrices.Last(i);
            var close = ctx.M15.ClosePrices.Last(i);
            var vol = ctx.M15.TickVolumes.Last(i);

            if (vol <= 0 || double.IsNaN(vol))
                continue;

            var typical = (high + low + close) / 3.0;
            var pv = typical * vol;

            sumPv += pv;
            sumV += vol;
            sumPv2 += pv * typical;
            bars++;
        }

        if (bars == 0 || sumV <= 0)
        {
            // No closed M15 bar at or after the anchor yet. Leave the cache keys unset
            // so the next M15 close retries, rather than pinning VWAP at zero for the
            // rest of the session the way the old per-day short-circuit did.
            ctx.Vwap = 0;
            ctx.VwapSd = 0;
            return;
        }

        ctx.Vwap = sumPv / sumV;
        var variance = sumPv2 / sumV - ctx.Vwap * ctx.Vwap;
        ctx.VwapSd = Math.Sqrt(Math.Max(0, variance));
        ctx.VwapDate = today;
        ctx.VwapBarOpen = currentBar;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTF — OPENING RANGE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// High and low of the first OpeningRangeMinutes M5 bars of the session the
    /// bot actually trades, anchored at SessionStartHourUtc/MinuteUtc.
    ///
    /// It was anchored at the London open, which put the range some 6.5 hours
    /// before the 13:00 UTC entry window, and its scan had an inverted bound that
    /// made found permanently false. Between the two, the leg fired 0 times in
    /// ~700 evaluations across a full month of telemetry and confluence was
    /// silently a 3-leg test. It is now anchored at the session start, so it is a
    /// genuine intraday retest of a level the market is still trading.
    ///
    /// The range is only valid once the whole window has closed. Until then
    /// OrValid is false, so a partially formed range is never read - anchoring at
    /// the session start would otherwise have turned the leg into a look-ahead.
    /// Cached per symbol per day, but only cached once it is usable.
    /// </summary>
    private void GetOpeningRange(SymbolContext ctx, DateTime utc)
    {
        var today = utc.Date;

        // Only short-circuit once the range is actually usable. The first call of
        // the day lands at the session start, before the range window has closed,
        // and caching that incomplete answer would disable the leg for the rest of
        // the session. The scan is a handful of bars, so re-deriving it each bar
        // until it completes costs nothing measurable.
        if (ctx.OrDate == today && ctx.OrValid)
            return;

        var start = today.AddMinutes(_sessionStartMin);
        var end = start.AddMinutes(OpeningRangeMinutes);

        double high = double.MinValue, low = double.MaxValue;
        var found = false;

        // Scan back from the resolved closed M5 bar. The window is in the past once
        // OrValid can be true, but starting at index 0 would let a forming bar into
        // the high and low on a feed that pre-creates bars.
        var start0 = ctx.M5ClosedIndex;
        if (start0 < 0)
            return;

        for (var i = start0; i < ctx.M5Bars; i++)
        {
            var openTime = ctx.M5.OpenTimes.Last(i);

            // Last(i) walks NEWEST -> OLDEST, so the window test has to skip
            // forward, not break. The original code broke on the first bar
            // whenever that bar was newer than the range end, which is every bar
            // of a window that is already in the past - so found stayed false,
            // OrValid stayed false, and the leg could never fire at all. The
            // VWAP scan above gets this right because it only has a lower bound.
            if (openTime < start)
                break;

            if (openTime >= end)
                continue;

            var h = ctx.M5.HighPrices.Last(i);
            var l = ctx.M5.LowPrices.Last(i);

            if (h > high) high = h;
            if (l < low) low = l;
            found = true;
        }

        ctx.OrDate = today;

        // The last bar inside the window opens at end - one M5 period and only
        // closes at end, so the range is not knowable until then.
        var complete = utc >= end;
        ctx.OrValid = complete && found && high > low;
        ctx.OrHigh = ctx.OrValid ? high : 0;
        ctx.OrLow = ctx.OrValid ? low : 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTF — STATIC SUPPORT / RESISTANCE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nearest swing low below <paramref name="price"/> over the last
    /// SwingLookbackM15 M15 bars. A pivot low is a bar whose low is the lowest of
    /// its PivotStrength neighbours either side.
    /// </summary>
    private double FindNearestSupport(SymbolContext ctx, double price)
    {
        double best = double.NaN;

        // Anchored to the resolved closed M15 bar so the swing search and the closed
        // M15 close it is measured against describe the same set of bars.
        var m15 = ctx.M15ClosedIndex;
        var bars = Math.Min(SwingLookbackM15, ctx.M15Bars - m15 - PivotStrength - 1);
        if (bars < PivotStrength + 1)
            return best;

        for (var i = PivotStrength; i < bars; i++)
        {
            var abs = m15 + i;
            var low = ctx.M15.LowPrices.Last(abs);
            if (low >= price)
                continue;

            if (!IsPivotLow(ctx.M15, abs, PivotStrength))
                continue;

            if (double.IsNaN(best) || low > best)
                best = low;
        }

        return best;
    }

    /// <summary>Nearest swing high above <paramref name="price"/>.</summary>
    private double FindNearestResistance(SymbolContext ctx, double price)
    {
        double best = double.NaN;
        var m15 = ctx.M15ClosedIndex;
        var bars = Math.Min(SwingLookbackM15, ctx.M15Bars - m15 - PivotStrength - 1);
        if (bars < PivotStrength + 1)
            return best;

        for (var i = PivotStrength; i < bars; i++)
        {
            var abs = m15 + i;
            var high = ctx.M15.HighPrices.Last(abs);
            if (high <= price)
                continue;

            if (!IsPivotHigh(ctx.M15, abs, PivotStrength))
                continue;

            if (double.IsNaN(best) || high < best)
                best = high;
        }

        return best;
    }

    private static bool IsPivotLow(Bars series, int index, int strength)
    {
        var value = series.LowPrices.Last(index);
        for (var j = 1; j <= strength; j++)
        {
            if (series.LowPrices.Last(index - j) <= value)
                return false;
            if (series.LowPrices.Last(index + j) <= value)
                return false;
        }

        return true;
    }

    private static bool IsPivotHigh(Bars series, int index, int strength)
    {
        var value = series.HighPrices.Last(index);
        for (var j = 1; j <= strength; j++)
        {
            if (series.HighPrices.Last(index - j) >= value)
                return false;
            if (series.HighPrices.Last(index + j) >= value)
                return false;
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTF — CONFLUENCE ZONE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts how many confluence levels sit within
    /// ConfluenceToleranceAtr x ATR(M15) of the just-closed M15 close.
    ///
    /// LONG legs:  VWAP (or VWAP - 1SD), static support, opening-range low,
    ///             H1 EMA20.
    /// SHORT legs: VWAP (or VWAP + 1SD), static resistance, opening-range high,
    ///             H1 EMA20.
    ///
    /// The zone price is the qualifying level nearest the close; the entry
    /// pattern is then required to close near THAT level.
    /// </summary>
    private Confluence FindConfluenceZone(SymbolContext ctx, Bias bias)
    {
        var result = new Confluence();

        var m15 = ctx.M15ClosedIndex;
        var close = ctx.M15.ClosePrices.Last(m15);
        var atr = ctx.M15Atr.Result.Last(m15);

        if (double.IsNaN(close) || atr <= 0 || ctx.Vwap <= 0)
            return result;

        var tolerance = ConfluenceToleranceAtr * atr;
        var bullish = bias == Bias.Bullish;

        // Leg 1 — VWAP, optionally the 1SD pullback extension.
        //
        // The band's name is a property of the LEVEL, so it is passed as the in-range
        // name and the below/above slots are left null. Previously this call passed
        // ("VWAP", "VWAP-1SD", "VWAP+1SD") and AddFactor relabelled by comparing the
        // level to price, so a long at the VWAP-1SD extension logged as "VWAP+1SD"
        // whenever price sat under it. The names encode an offset from VWAP while the
        // relabel tested an offset from price; the two are unrelated.
        string vwapName;
        var vwapLevel = ctx.Vwap;

        if (UseVwapSdPullback && ctx.VwapSd > 0)
        {
            vwapLevel = bullish ? ctx.Vwap - ctx.VwapSd : ctx.Vwap + ctx.VwapSd;
            vwapName = bullish ? "VWAP-1SD" : "VWAP+1SD";
        }
        else
        {
            vwapName = "VWAP";
        }

        AddFactor(result, vwapLevel, close, tolerance, vwapName, null, null);

        // Leg 2 — static support / resistance.
        var sr = bullish ? FindNearestSupport(ctx, close) : FindNearestResistance(ctx, close);
        if (!double.IsNaN(sr))
            AddFactor(result, sr, close, tolerance, bullish ? "S/R-support" : "S/R-resistance", null, null);

        // Leg 3 — opening-range retest.
        if (ctx.OrValid)
        {
            var orLevel = bullish ? ctx.OrLow : ctx.OrHigh;
            AddFactor(result, orLevel, close, tolerance, bullish ? "OR-low" : "OR-high", null, null);
        }

        // Leg 4 — the H1 EMA20 projected onto the M15 chart.
        var ema20 = ctx.H1EmaFast.Result.Last(ctx.H1ClosedIndex);
        if (!double.IsNaN(ema20))
            AddFactor(result, ema20, close, tolerance, "H1-EMA20", null, null);

        result.IsValid = result.Count >= MinConfluenceFactors;
        return result;
    }

    /// <summary>
    /// Records a level as a confluence factor when it lies within tolerance of
    /// price. Keeps the nearest qualifying level as the zone price.
    /// </summary>
    private static void AddFactor(Confluence result, double level, double price, double tolerance,
        string inRangeName, string belowName, string aboveName)
    {
        if (double.IsNaN(level) || level <= 0)
            return;

        var distance = Math.Abs(level - price);
        if (distance > tolerance)
            return;

        // Name the band the level belongs to, for the log line.
        var name = inRangeName;
        if (level < price && belowName != null)
            name = belowName;
        else if (level > price && aboveName != null)
            name = aboveName;

        result.Count++;
        result.Factors.Add(name);

        if (result.Count == 1 || distance < Math.Abs(result.ZonePrice - price))
            result.ZonePrice = level;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTF — RSI DIVERGENCE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Divergence between price and RSI(14) over the last RsiDivergenceLookback
    /// M15 bars, plus the weaker "extreme and turning" fallback the spec allows.
    ///
    /// Bullish: a recent pivot low in price sits BELOW the older pivot low while
    ///           RSI at that low sits ABOVE its older value.
    /// Bearish: mirrored on highs.
    /// </summary>
    private bool CheckRSIDivergence(SymbolContext ctx, Bias bias, out string label)
    {
        label = null;

        var rsi = ctx.M15Rsi.Result;
        var lookback = RsiDivergenceLookback;

        // Anchor the whole divergence window to the resolved closed M15 bar. Reading
        // index 0 here put a partially formed M15 candle into both the RSI values and
        // the pivot highs/lows it is compared against.
        var m15 = ctx.M15ClosedIndex;

        if (m15 < 0 || rsi.Count <= m15 + lookback + PivotStrength + 1)
            return false;

        var latest = rsi.Last(m15);
        var prior = rsi.Last(m15 + 1);

        if (double.IsNaN(latest) || double.IsNaN(prior))
            return false;

        if (bias == Bias.Bullish)
        {
            var found = FindDivergence(ctx, true, out var type);
            if (found)
            {
                label = type;
                return true;
            }

            if (latest < RsiOversoldLevel && latest > prior)
            {
                label = $"RSI {latest:0.0}<{RsiOversoldLevel:0} rising";
                return true;
            }

            return false;
        }

        var foundBear = FindDivergence(ctx, false, out var bearType);
        if (foundBear)
        {
            label = bearType;
            return true;
        }

        if (latest > RsiOverboughtLevel && latest < prior)
        {
            label = $"RSI {latest:0.0}>{RsiOverboughtLevel:0} falling";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compares the two most recent pivots in the search window and reports
    /// whether price and RSI disagree. Price uses the pivot's own high/low, not
    /// the close, because a pivot is defined by its extreme.
    /// </summary>
    private bool FindDivergence(SymbolContext ctx, bool bullish, out string label)
    {
        label = null;

        var rsi = ctx.M15Rsi.Result;

        // Same closed-bar anchor as CheckRSIDivergence: every index below is an offset
        // back from the closed M15 bar, applied identically to the price series and to
        // the RSI result so the two stay comparable.
        var m15 = ctx.M15ClosedIndex;
        var search = Math.Min(RsiDivergenceLookback, ctx.M15Bars - m15 - PivotStrength - 1);
        if (search < PivotStrength * 2 + 2)
            return false;

        var recentPivot = -1;
        var olderPivot = -1;

        for (var i = PivotStrength; i < search; i++)
        {
            var abs = m15 + i;
            var isPivot = bullish ? IsPivotLow(ctx.M15, abs, PivotStrength) : IsPivotHigh(ctx.M15, abs, PivotStrength);
            if (!isPivot)
                continue;

            var rsiValue = rsi.Last(abs);
            if (double.IsNaN(rsiValue) || rsiValue <= 0)
                continue;

            if (recentPivot < 0)
                recentPivot = abs;
            else
            {
                olderPivot = abs;
                break;
            }
        }

        if (recentPivot < 0 || olderPivot < 0)
            return false;

        var priceSeries = bullish ? ctx.M15.LowPrices : ctx.M15.HighPrices;
        var recentPrice = priceSeries.Last(recentPivot);
        var olderPrice = priceSeries.Last(olderPivot);
        var recentRsi = rsi.Last(recentPivot);
        var olderRsi = rsi.Last(olderPivot);

        if (bullish && recentPrice < olderPrice && recentRsi > olderRsi)
        {
            label = $"bullish-div (px {recentPrice:0.#####}<{olderPrice:0.#####}, rsi {recentRsi:0.0}>{olderRsi:0.0})";
            return true;
        }

        if (!bullish && recentPrice > olderPrice && recentRsi < olderRsi)
        {
            label = $"bearish-div (px {recentPrice:0.#####}>{olderPrice:0.#####}, rsi {recentRsi:0.0}<{olderRsi:0.0})";
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LTF — STOCHASTIC RSI
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hand-rolled Stochastic RSI over cTrader's built-in M5 RSI.
    ///
    ///   %K     = 100 x (RSI - min(RSI, K)) / (max(RSI, K) - min(RSI, K))
    ///   %D     = SMA(%K, DPeriod)
    ///   slow%D = SMA(%D, Slowing)
    ///
    /// cTrader has no StochasticRSI indicator, and the built-in
    /// StochasticOscillator cannot be fed the RSI series (it takes Bars
    /// or Bars, and Bars has no public constructor), so the stochastic
    /// stage is computed here over a double[] copy of the RSI output.
    ///
    /// The buffers are laid out oldest-index-last with the newest sample at
    /// NewestIndex. Getting this alignment wrong is the look-ahead risk of the
    /// whole file, so the crossover test below reads NewestIndex and
    /// NewestIndex + 1 and nothing else.
    ///
    /// Buffer index 0 is filled from the resolved closed M5 bar rather than series
    /// index 0, which means every downstream stage inherits the closed-bar anchor
    /// without change: NewestIndex is the closed bar, NewestIndex + 1 is the bar
    /// before it, and IsStochRsiTrigger still means "crossed on the latest closed
    /// bar". Only the read in the fill loop is offset, so the relative meaning of
    /// every index inside the %K / %D / slow%D chain is preserved.
    /// </summary>
    private StochRsiState CheckStochRsi(SymbolContext ctx)
    {
        var state = new StochRsiState();

        var rsi = ctx.M5Rsi.Result;
        var newest = SymbolContext.NewestIndex;

        // Offset from series index to the resolved closed M5 bar, so buffer index 0
        // lands on the newest bar that had already closed when the chart's M5 bar
        // closed. Zero on the M5 series today, but resolved rather than assumed.
        var offset = ctx.M5ClosedIndex;

        // Samples actually consumed: %K(K) + %D(D) + slow%D(S) down to
        // NewestIndex, plus one extra bar of %K and %D history so that
        // PrevSlowD at NewestIndex + 1 is defined.
        var need = StochRsiKPeriod + StochRsiDPeriod + StochRsiSlowing + newest + 1;

        // A stage is only defined while its whole window is inside the buffer,
        // so the last writable index shrinks as the stages chain. Using the
        // full buffer length here instead runs off the end of the array.
        var kMax = need - StochRsiKPeriod;
        var dMax = kMax - StochRsiDPeriod + 1;
        var sMax = dMax - StochRsiSlowing + 1;

        if (kMax < 0 || dMax < newest + 1 || sMax < newest + 1)
            return state;

        if (rsi.Count < need + offset || ctx.RsiBuf.Length < need)
            return state;

        for (var i = 0; i < need; i++)
        {
            var src = rsi.Last(offset + i);
            if (double.IsNaN(src))
                return state;
            ctx.RsiBuf[i] = src;
        }

        // %K
        for (var i = 0; i <= kMax; i++)
        {
            var hi = double.MinValue;
            var lo = double.MaxValue;

            for (var j = 0; j < StochRsiKPeriod; j++)
            {
                var v = ctx.RsiBuf[i + j];
                if (v > hi) hi = v;
                if (v < lo) lo = v;
            }

            ctx.KBuf[i] = hi - lo <= double.Epsilon ? 50.0 : 100.0 * (ctx.RsiBuf[i] - lo) / (hi - lo);
        }

        // %D = SMA(%K)
        for (var i = 0; i <= dMax; i++)
        {
            double sum = 0;
            for (var j = 0; j < StochRsiDPeriod; j++)
                sum += ctx.KBuf[i + j];
            ctx.DBuf[i] = sum / StochRsiDPeriod;
        }

        // slow %D = SMA(%D)
        for (var i = 0; i <= sMax; i++)
        {
            double sum = 0;
            for (var j = 0; j < StochRsiSlowing; j++)
                sum += ctx.DBuf[i + j];
            ctx.SlowDBuf[i] = sum / StochRsiSlowing;
        }

        state.K = ctx.KBuf[newest];
        state.D = ctx.DBuf[newest];
        state.SlowD = ctx.SlowDBuf[newest];
        state.PrevSlowD = ctx.SlowDBuf[newest + 1];
        state.IsValid = true;
        return state;
    }

    /// <summary>
    /// Entry trigger: slow %D crossing out of the oversold band upward (long) or
    /// out of the overbought band downward (short), on the just-closed bar.
    /// </summary>
    private bool IsStochRsiTrigger(StochRsiState state, Bias bias)
    {
        if (!state.IsValid)
            return false;

        if (bias == Bias.Bullish)
            return state.PrevSlowD <= StochRsiOversold && state.SlowD > StochRsiOversold;

        return state.PrevSlowD >= StochRsiOverbought && state.SlowD < StochRsiOverbought;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LTF — CANDLESTICK PATTERNS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects a bullish or bearish entry candle on the just-closed M5 bar.
    /// Long:  bullish engulfing, or a hammer (lower wick > PinBarWickRatio x body).
    /// Short: bearish engulfing, or a shooting star (upper wick > ratio x body).
    /// </summary>
    private CandleSignal DetectCandlePattern(SymbolContext ctx)
    {
        if (ctx.M5Bars < 2)
            return CandleSignal.None;

        var m5 = ctx.M5ClosedIndex;

        var o0 = ctx.M5.OpenPrices.Last(m5);
        var h0 = ctx.M5.HighPrices.Last(m5);
        var l0 = ctx.M5.LowPrices.Last(m5);
        var c0 = ctx.M5.ClosePrices.Last(m5);
        var o1 = ctx.M5.OpenPrices.Last(m5 + 1);
        var c1 = ctx.M5.ClosePrices.Last(m5 + 1);

        var range = h0 - l0;
        if (range <= 0)
            return CandleSignal.None;

        var body = Math.Abs(c0 - o0);
        var upperWick = h0 - Math.Max(c0, o0);
        var lowerWick = Math.Min(c0, o0) - l0;

        // Engulfing needs a real body, not a doji.
        if (body >= EngulfingMinBodyRatio * range)
        {
            if (c1 < o1 && c0 > o0 && c0 >= o1 && o0 <= c1)
                return CandleSignal.Bullish;

            if (c1 > o1 && c0 < o0 && c0 <= o1 && o0 >= c1)
                return CandleSignal.Bearish;
        }

        // Hammer: long lower wick, small upper wick, body in the upper third.
        if (lowerWick >= PinBarWickRatio * body && upperWick <= body && c0 > o0 && body / range >= 0.25)
            return CandleSignal.Bullish;

        // Shooting star: long upper wick, small lower wick, body in the lower third.
        if (upperWick >= PinBarWickRatio * body && lowerWick <= body && c0 < o0 && body / range >= 0.25)
            return CandleSignal.Bearish;

        return CandleSignal.None;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SPREAD FILTER
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rejects when the live spread exceeds SpreadMaxMultiplier x the sampled
    /// 20-bar average. Until the ring buffer is full the filter stays silent
    /// rather than judging against a 3-sample mean.
    /// </summary>
    private bool PassesSpreadFilter(SymbolContext ctx, out double currentPips)
    {
        currentPips = ctx.Symbol.Spread / ctx.Symbol.PipSize;

        if (!UseSpreadFilter)
            return true;

        if (ctx.SpreadSamples < ctx.SpreadRing.Length)
            return true;

        return currentPips <= ctx.SpreadAveragePips * SpreadMaxMultiplier;
    }
}
