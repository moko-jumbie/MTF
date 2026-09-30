using System;

namespace cAlgo.Robots;

// ─────────────────────────────────────────────────────────────────────────────
// MTF.Risk — position sizing and circuit breakers.
//
// Sizing uses cTrader's native VolumeForFixedRisk against the PER-SYMBOL Symbol
// object, never the chart symbol: the chart symbol's tick value and pip size
// would mis-size a GBPUSD trade by an order of magnitude.
//
// Risk is sized on Account.Equity so an open loss shrinks the next position.
// ─────────────────────────────────────────────────────────────────────────────
public partial class MTF
{
    private struct RiskPlan
    {
        public bool IsValid;
        public string RejectReason;
        public TradeType TradeType;
        public double VolumeInUnits;
        public double EntryPrice;
        public double StopLoss;
        public double TakeProfit1;
        public double TakeProfit2;
        public double RiskPips;
        public double AtrPips;
        public double Tp1Pips;
        public double Tp2Pips;
    }

    /// <summary>
    /// Builds the full risk plan for a candidate trade: entry reference, ATR stop,
    /// two take-profit levels and the volume that risks RiskPercent of equity.
    /// Returns an invalid plan with a reason rather than throwing, so the reject
    /// log always names the gate that stopped the trade.
    /// </summary>
    private RiskPlan CalculateRisk(SymbolContext ctx, Bias bias)
    {
        var plan = new RiskPlan { TradeType = bias == Bias.Bullish ? TradeType.Buy : TradeType.Sell };

        var symbol = ctx.Symbol;

        if (!symbol.MarketHours.IsOpened())
        {
            plan.RejectReason = "market-closed";
            return plan;
        }

        var atrPrice = ctx.M5Atr.Result.Last(ctx.M5ClosedIndex);
        if (double.IsNaN(atrPrice) || atrPrice <= 0)
        {
            plan.RejectReason = "atr-invalid";
            return plan;
        }

        // Snap every pip distance to the symbol's tick grid FIRST. cTrader 1.0.21
        // has no NormalizePips, so this is what keeps the pip values handed to
        // ExecuteMarketOrder and the derived price levels on a valid grid.
        // RoundingMode.ToNearest here is safe: the risk cap is enforced later on
        // the volume, not on the distance.
        var pipStep = symbol.TickSize / symbol.PipSize;
        var riskPips = SnapToPipStep(atrPrice * SlAtrMultiplier / symbol.PipSize, pipStep);

        if (riskPips <= 0)
        {
            plan.RejectReason = "stop-pips-zero";
            return plan;
        }

        // The broker rejects a stop or target inside its minimum distance. On a
        // very quiet pair 1 x ATR(M5) can be tighter than that, and the entry
        // would fail with InvalidStopLossTakeProfit instead of being skipped.
        if (riskPips < symbol.MinStopLossDistance)
        {
            plan.RejectReason = $"stop-inside-min-distance ({riskPips:0.##}p < {symbol.MinStopLossDistance:0.##}p)";
            return plan;
        }

        plan.AtrPips = atrPrice / symbol.PipSize;
        plan.RiskPips = riskPips;
        plan.Tp1Pips = SnapToPipStep(riskPips * Tp1R, pipStep);
        plan.Tp2Pips = SnapToPipStep(riskPips * Tp2R, pipStep);

        if (plan.Tp2Pips < symbol.MinTakeProfitDistance)
        {
            plan.RejectReason = $"tp2-inside-min-distance ({plan.Tp2Pips:0.##}p < {symbol.MinTakeProfitDistance:0.##}p)";
            return plan;
        }

        var stopDistance = riskPips * symbol.PipSize;

        // Rounded to Digits so the levels are valid absolute prices.
        plan.EntryPrice = Math.Round(plan.TradeType == TradeType.Buy ? symbol.Ask : symbol.Bid, symbol.Digits);
        plan.StopLoss = Math.Round(
            plan.TradeType == TradeType.Buy ? plan.EntryPrice - stopDistance : plan.EntryPrice + stopDistance,
            symbol.Digits);
        plan.TakeProfit1 = Math.Round(
            plan.TradeType == TradeType.Buy ? plan.EntryPrice + plan.Tp1Pips * symbol.PipSize : plan.EntryPrice - plan.Tp1Pips * symbol.PipSize,
            symbol.Digits);
        plan.TakeProfit2 = Math.Round(
            plan.TradeType == TradeType.Buy ? plan.EntryPrice + plan.Tp2Pips * symbol.PipSize : plan.EntryPrice - plan.Tp2Pips * symbol.PipSize,
            symbol.Digits);

        // Stop on the losing side, targets on the winning side - asserted, not assumed.
        if (plan.TradeType == TradeType.Buy && !(plan.StopLoss < plan.EntryPrice && plan.TakeProfit1 > plan.EntryPrice && plan.TakeProfit2 > plan.TakeProfit1))
        {
            plan.RejectReason = "level-order-invalid";
            return plan;
        }

        if (plan.TradeType == TradeType.Sell && !(plan.StopLoss > plan.EntryPrice && plan.TakeProfit1 < plan.EntryPrice && plan.TakeProfit2 < plan.TakeProfit1))
        {
            plan.RejectReason = "level-order-invalid";
            return plan;
        }

        var riskAmount = Account.Equity * RiskPercent / 100.0;
        if (riskAmount <= 0)
        {
            plan.RejectReason = "no-equity";
            return plan;
        }

        var raw = symbol.VolumeForFixedRisk(riskAmount, plan.RiskPips);
        if (raw <= 0 || double.IsNaN(raw))
        {
            plan.RejectReason = "sizing-failed";
            return plan;
        }

        // RoundingMode.Down only. ToNearest can round UP past the risk cap, which
        // silently breaks the risk model across an entire optimisation sweep.
        var volume = symbol.NormalizeVolumeInUnits(raw, RoundingMode.Down);

        if (raw > symbol.VolumeInUnitsMax)
            volume = symbol.NormalizeVolumeInUnits(symbol.VolumeInUnitsMax, RoundingMode.Down);

        // Min-volume risk creep: the correct size is below the symbol minimum, so
        // flooring it at the minimum would risk far more than RiskPercent. Skip.
        if (volume < symbol.VolumeInUnitsMin)
        {
            plan.RejectReason = $"below-min-volume (needs {raw:0} units, min {symbol.VolumeInUnitsMin:0})";
            return plan;
        }

        if (volume > symbol.VolumeInUnitsMax)
        {
            plan.RejectReason = "above-max-volume";
            return plan;
        }

        var requiredMargin = symbol.GetEstimatedMargin(plan.TradeType, volume);
        if (requiredMargin > Account.FreeMargin)
        {
            plan.RejectReason = $"insufficient-margin (need {requiredMargin:0.00}, free {Account.FreeMargin:0.00})";
            return plan;
        }

        plan.VolumeInUnits = volume;
        plan.IsValid = true;
        return plan;
    }

    /// <summary>
    /// Rounds a pip distance onto the symbol's tick grid expressed in pips
    /// (TickSize / PipSize - 0.1 pips on a 5-digit EURUSD, 1.0 on a 3-digit one).
    /// Away-from-zero is used so a stop is never snapped tighter than planned.
    /// </summary>
    private static double SnapToPipStep(double pips, double pipStep)
    {
        if (pipStep <= 0 || double.IsNaN(pips) || double.IsInfinity(pips))
            return pips;

        return Math.Round(pips / pipStep, MidpointRounding.AwayFromZero) * pipStep;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CIRCUIT BREAKERS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Daily loss circuit breaker. Blocks NEW entries for the rest of the day
    /// once equity has fallen DailyLossLimitPercent below the day's starting
    /// equity. The end-of-session flatten is unaffected, so the bot still ends
    /// flat.
    /// </summary>
    private bool CheckDailyLossLimit()
    {
        if (_dayStartEquity <= 0)
            return false;

        var loss = _dayStartEquity - Account.Equity;
        var limit = _dayStartEquity * DailyLossLimitPercent / 100.0;

        if (loss < limit)
            return false;

        if (!_dailyLossTripped)
        {
            _dailyLossTripped = true;
            Print($"CIRCUIT BREAKER: daily loss {loss:0.00} {Account.Asset.Name} reached the {DailyLossLimitPercent:0.##}% limit (start {_dayStartEquity:0.00}). No new entries today. Existing positions are still managed and will be flattened at {SessionEndHourUtc:00}:{SessionEndMinuteUtc:00} UTC.");
        }

        return true;
    }

    /// <summary>
    /// Rolls the daily counters over when the UTC date changes. Must not depend on
    /// the cBot having been restarted at midnight, so it compares the stored date
    /// against the live one on every tick and bar.
    /// </summary>
    private void EnsureDayRollover()
    {
        var today = TimeInUtc.Date;
        if (today == _trackedDate)
            return;

        if (_trackedDate != DateTime.MinValue && _tradesToday > 0)
            PrintSessionSummary("rollover");

        ResetDailyState(today);
    }

    /// <summary>
    /// Resets the trade counter, the daily P&amp;L tracker and the loss breaker.
    /// Day-start equity is taken from the CURRENT balance, not restored from
    /// history, so a restart mid-day measures from where the account actually is.
    /// </summary>
    private void ResetDailyState(DateTime today)
    {
        _trackedDate = today;
        _dayStartEquity = Account.Equity;
        _dayStartBalance = Account.Balance;
        _tradesToday = 0;
        _winsToday = 0;
        _lossesToday = 0;
        _netPipsToday = 0;
        _netProfitToday = 0;
        _rSumToday = 0.0;
        _rCountToday = 0;
        _dailyLossTripped = false;
        _eodFlushedOn = null;

        // The gate counters are per-day diagnostics, so they have to roll with
        // the day. Left alone they accumulate for the whole backtest and every
        // "session-end" report prints lifetime totals.
        foreach (var c in _contexts.Values)
        {
            c.Evaluations = 0;
            c.AlignmentSkips = 0;
            c.PassSpread = 0;
            c.PassBias = 0;
            c.PassConfluence = 0;
            c.PassScore = 0;
            c.PassRisk = 0;
            c.Entries = 0;
        }
    }

    /// <summary>Records a filled entry against the daily trade budget.</summary>
    private void RegisterTradeFilled()
    {
        _tradesToday++;
        Print($"Trades today: {_tradesToday}/{MaxTradesPerDay}.");
    }

    /// <summary>True once the daily trade budget is exhausted.</summary>
    private bool IsTradeBudgetExhausted() => _tradesToday >= MaxTradesPerDay;
}
