using System;
using System.Globalization;
using System.Text;

namespace cAlgo.Robots;

// ─────────────────────────────────────────────────────────────────────────────
// MTF.Execution — signal orchestration, order placement and position management.
//
// EXIT DESIGN: the broker-side TakeProfit sits at TP2 on the FULL position, and
// TP1 is taken manually as a partial close. Putting the broker TP at TP1 would
// liquidate the whole ticket at 1.5R and make the TP2 leg impossible.
//
// Requires a HEDGING account: ClosePosition(Position, volume) is a real partial
// close, which a netted account does not support.
// ─────────────────────────────────────────────────────────────────────────────
public partial class MTF
{
    private const string CommentPrefix = "MTF";
    private const int CommentVersion = 1;

    // ─────────────────────────────────────────────────────────────────────────
    // SIGNAL ORCHESTRATION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full top-down confluence chain for one symbol on one closed M5
    /// bar. Cheap gates run first; the first failure logs the exact reason and
    /// returns, so the reject log always names a single blocking condition.
    /// </summary>
    /// <summary>
    /// Evaluates one symbol on the just-closed M5 bar and opens a position when the
    /// setup score clears <see cref="MinSetupScore"/>.
    ///
    /// Only two things are hard: the H1 trend must have a direction, and the
    /// confluence count must reach MinConfluenceFactors. Everything else in the
    /// original spec (ADX strength, RSI divergence, StochRSI, candle, zone proximity,
    /// tick volume) contributes points. Seven hard ANDs multiplied out to ~3e-6 per
    /// evaluation, which is why a month of backtests produced zero trades.
    /// Every stage also increments a per-symbol counter so the end-of-run report can
    /// state the measured pass rate of each gate.
    /// </summary>
    private void EvaluateSymbol(SymbolContext ctx)
    {
        if (!IsContextReady(ctx))
            return;

        ctx.Evaluations++;

        if (!PassesSpreadFilter(ctx, out var spreadPips))
        {
            Reject(ctx, $"spread {spreadPips:0.0}p vs avg {ctx.SpreadAveragePips:0.0}p");
            return;
        }

        ctx.PassSpread++;

        var bias = ReadTrendBias(ctx, out var adxValue);
        ctx.LastBias = bias;
        ctx.LastAdx = adxValue;

        if (bias == Bias.None)
        {
            Reject(ctx, $"no H1 direction (adx {adxValue:0.0}, closed-bar ema20/50 " +
                        $"{ctx.H1EmaFast.Result.Last(ctx.H1ClosedIndex):0.#####}/{ctx.H1EmaSlow.Result.Last(ctx.H1ClosedIndex):0.#####} " +
                        $"@ H1 {ctx.H1.OpenTimes.Last(ctx.H1ClosedIndex):yyyy-MM-dd HH:mm})");
            return;
        }

        ctx.PassBias++;

        var utc = TimeInUtc;
        CalculateSessionVWAP(ctx, utc);
        GetOpeningRange(ctx, utc);

        if (ctx.Vwap <= 0)
        {
            Reject(ctx, "no session VWAP yet");
            return;
        }

        var confluence = FindConfluenceZone(ctx, bias);
        ctx.LastZoneSummary = $"zone {confluence.Count}/{MinConfluenceFactors} [{string.Join("+", confluence.Factors)}]";

        if (!confluence.IsValid)
        {
            Reject(ctx, $"confluence {confluence.Count}/{MinConfluenceFactors} " +
                        $"[{string.Join(",", confluence.Factors)}] need {MinConfluenceFactors}");
            return;
        }

        ctx.PassConfluence++;

        // ── scored legs ────────────────────────────────────────────────────────
        var score = 0.0;
        var legs = new StringBuilder();

        if (adxValue >= AdxThreshold)
        {
            score += ScoreTrendStrength;
            AppendLeg(legs, $"adx {adxValue:0.0}>={AdxThreshold:0} +{ScoreTrendStrength:0.0}");
        }

        var extra = Math.Min((confluence.Count - MinConfluenceFactors) * ScoreConfluenceExtra, ScoreConfluenceExtraCap);
        if (extra > 0)
        {
            score += extra;
            AppendLeg(legs, $"confluence+{confluence.Count - MinConfluenceFactors} +{extra:0.0}");
        }

        var rsiLabel = (string)null;
        var stoch = new StochRsiState();
        var volumeRatio = 1.0;

        try
        {
            rsiLabel = ScoreRsiLeg(ctx, bias, legs, ref score);

            stoch = CheckStochRsi(ctx);
            ScoreStochLeg(stoch, bias, legs, ref score);

            if (UseCandlePattern)
            {
                var candle = DetectCandlePattern(ctx);
                var wanted = bias == Bias.Bullish ? CandleSignal.Bullish : CandleSignal.Bearish;

                if (candle == wanted)
                {
                    score += ScoreCandlePattern;
                    AppendLeg(legs, $"{wanted} candle +{ScoreCandlePattern:0.0}");
                }
            }

            var m5Close = ctx.M5.ClosePrices.Last(ctx.M5ClosedIndex);
            var proximity = ZoneProximityBand(ctx);

            if (Math.Abs(m5Close - confluence.ZonePrice) <= proximity)
            {
                score += ScoreZoneProximity;
                AppendLeg(legs, $"at zone {proximity:0.#####} +{ScoreZoneProximity:0.0}");
            }

            volumeRatio = ScoreVolumeLeg(ctx, legs, ref score);
        }
        catch (Exception ex)
        {
            Print($"SIGNAL FAULT {ctx.Name} @ {TimeInUtc:yyyy-MM-dd HH:mm} UTC - {ex.GetType().Name} in scored legs: {ex.Message}");
            Print(ex.ToString());
            Reject(ctx, $"signal fault {ex.GetType().Name}");
            return;
        }

        if (score < MinSetupScore)
        {
            Reject(ctx, $"score {score:0.0}<{MinSetupScore:0.0} [{legs}]");
            return;
        }

        ctx.PassScore++;

        var plan = CalculateRisk(ctx, bias);
        if (!plan.IsValid)
        {
            Reject(ctx, "risk: " + plan.RejectReason);
            return;
        }

        ctx.PassRisk++;

        OpenPosition(ctx, plan, adxValue, rsiLabel, stoch, volumeRatio, confluence, spreadPips, score, legs.ToString());
    }

    private static void AppendLeg(StringBuilder legs, string text)
    {
        if (legs.Length > 0)
            legs.Append(", ");

        legs.Append(text);
    }

    /// <summary>
    /// Width of the "closed at the zone" band, in price.
    ///
    /// This is the smaller of the two natural scales on purpose. The zone level
    /// was already accepted because it sits within ConfluenceToleranceAtr x
    /// ATR(M15) of the M15 close, so a band of that size or wider is satisfied by
    /// construction and the leg scores on every setup that clears confluence -
    /// measured at 8 of the first 10 scored setups, which made it a free +1 that
    /// quietly lowered the real entry threshold. ZoneProximityShare scales it down
    /// so the leg has to be earned by closing near the level.
    /// </summary>
    private double ZoneProximityBand(SymbolContext ctx)
    {
        var atrM5 = ctx.M5Atr.Result.Last(ctx.M5ClosedIndex);
        var atrM15 = ctx.M15Atr.Result.Last(ctx.M15ClosedIndex);

        var atrBand = PatternProximityAtr * atrM5;
        var toleranceBand = ConfluenceToleranceAtr * atrM15 * ZoneProximityShare;

        return Math.Min(atrBand, toleranceBand);
    }

    /// <summary>
    /// M15 RSI leg. A real price/RSI divergence scores full, the oversold/overbought
    /// extreme turning back in the trade's direction scores half. Neither is fatal.
    /// </summary>
    private string ScoreRsiLeg(SymbolContext ctx, Bias bias, StringBuilder legs, ref double score)
    {
        if (!CheckRSIDivergence(ctx, bias, out var label))
            return label;

        // CheckRSIDivergence returns true for both the divergence and the extreme
        // fallback; the label text is the only way to tell which one fired.
        var isDivergence = label.StartsWith("bullish-div", StringComparison.Ordinal)
                           || label.StartsWith("bearish-div", StringComparison.Ordinal);

        if (isDivergence)
        {
            score += ScoreRsiDivergence;
            AppendLeg(legs, $"{label} +{ScoreRsiDivergence:0.0}");
        }
        else
        {
            score += ScoreRsiExtreme;
            AppendLeg(legs, $"{label} +{ScoreRsiExtreme:0.0}");
        }

        return label;
    }

    /// <summary>
    /// M5 StochRSI leg. A band CROSS on the closed bar scores full; simply being in
    /// the zone and turning with the trend scores half. The old version made the
    /// cross mandatory, and a cross on one specific bar is roughly a 2% event, which
    /// was on its own enough to guarantee a zero-trade month.
    /// </summary>
    private void ScoreStochLeg(StochRsiState stoch, Bias bias, StringBuilder legs, ref double score)
    {
        if (!stoch.IsValid)
            return;

        if (IsStochRsiTrigger(stoch, bias))
        {
            score += ScoreStochCross;
            AppendLeg(legs, $"stoch cross +{ScoreStochCross:0.0}");
            return;
        }

        var bullish = bias == Bias.Bullish;
        var turning = bullish ? stoch.SlowD > stoch.PrevSlowD : stoch.SlowD < stoch.PrevSlowD;
        var inZone = bullish ? stoch.SlowD < 30.0 : stoch.SlowD > 70.0;

        if (turning && inZone)
        {
            score += ScoreStochInZone;
            AppendLeg(legs, $"stoch in zone {stoch.SlowD:0.0} turning +{ScoreStochInZone:0.0}");
        }
    }

    /// <summary>
    /// Tick-activity leg, scored rather than enforced. Returns the measured ratio so
    /// the entry log can still report it.
    /// </summary>
    private double ScoreVolumeLeg(SymbolContext ctx, StringBuilder legs, ref double score)
    {
        if (!UseVolumeFilter)
            return 1.0;

        if (ctx.M5Bars < VolumeAvgBars + 2)
            return 1.0;

        // "Current" is the resolved closed M5 bar, and the average is taken from the
        // bars immediately before it, so the ratio no longer depends on the forming
        // bar having been created yet when OnBarClosed fires.
        var closed = ctx.M5ClosedIndex;
        if (closed < 0)
            return 1.0;

        double sum = 0;
        for (var i = 1; i <= VolumeAvgBars; i++)
            sum += ctx.M5.TickVolumes.Last(closed + i);

        var average = sum / VolumeAvgBars;
        if (average <= 0)
            return 1.0;

        var current = ctx.M5.TickVolumes.Last(closed);
        if (current <= 0)
            return 1.0;

        var ratio = current / average;

        if (ratio >= VolumeMultiplier)
        {
            score += ScoreTickVolume;
            AppendLeg(legs, $"ticks {ratio:0.00}x>={VolumeMultiplier:0.0}x +{ScoreTickVolume:0.0}");
        }

        return ratio;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ENTRY
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Places the market entry with a stop at 1 x ATR(M5) and the broker-side
    /// take-profit at TP2. Persists the ATR and R distances in the position
    /// comment so a restart can rebuild the TP1/breakeven/trail state.
    /// </summary>
    private void OpenPosition(SymbolContext ctx, RiskPlan plan, double adxValue, string rsiLabel,
        StochRsiState stoch, double volumeRatio, Confluence confluence, double spreadPips, double score, string legs)
    {
        if (CheckDailyLossLimit())
        {
            Reject(ctx, "daily-loss-limit tripped");
            return;
        }

        if (IsTradeBudgetExhausted())
        {
            Reject(ctx, "max-trades-per-day reached");
            return;
        }

        var comment = string.Format(CultureInfo.InvariantCulture, "{0}|v{1}|vol={2:0}|atr={3:0.####}|r={4:0.####}|tp1={5:0.####}|tp2={6:0.####}",
            CommentPrefix, CommentVersion, plan.VolumeInUnits, plan.AtrPips, plan.RiskPips, plan.Tp1Pips, plan.Tp2Pips);

        // pips arguments, converted from the price distances and snapped to
        // Digits via the inverse. cTrader 1.0.21 has no NormalizePips.
        var slPips = plan.RiskPips;
        var tpPips = plan.Tp2Pips;

        var result = ExecuteMarketOrder(plan.TradeType, ctx.Name, plan.VolumeInUnits, Label, slPips, tpPips, comment);

        if (!result.IsSuccessful)
        {
            Print($"ENTRY FAILED {ctx.Name} {plan.TradeType}: {result.Error} | vol={plan.VolumeInUnits:0} sl={slPips:0.##}p tp={tpPips:0.##}p");
            return;
        }

        var position = result.Position;

        _positionStates[position.Id] = new PositionState
        {
            Id = position.Id,
            SymbolName = ctx.Name,
            TradeType = plan.TradeType,
            EntryPrice = position.EntryPrice,
            AtrPips = plan.AtrPips,
            RiskPips = plan.RiskPips,
            Tp1Pips = plan.Tp1Pips,
            Tp2Pips = plan.Tp2Pips,
            OriginalVolume = plan.VolumeInUnits,
            Tp1Done = false,
            BreakEvenDone = false
        };

        RegisterTradeFilled();
        ResetNearMiss(ctx);

        var sb = new StringBuilder();
        sb.AppendLine($"ENTRY  {ctx.Name} {plan.TradeType}  vol={plan.VolumeInUnits:0} units ({ctx.Symbol.VolumeInUnitsToQuantity(plan.VolumeInUnits):0.00} lots)");
        sb.AppendLine($"       entry={position.EntryPrice:0.#####}  SL={plan.StopLoss:0.#####} ({slPips:0.##}p)  TP1={plan.TakeProfit1:0.#####} ({plan.Tp1Pips:0.##}p, {Tp1CloseFraction:0.##} close)  TP2={plan.TakeProfit2:0.#####} ({plan.Tp2Pips:0.##}p)");
        sb.AppendLine($"       score {score:0.0}/{MinSetupScore:0.0}: {legs}");
        sb.AppendLine($"       confluence {confluence.Count}/{MinConfluenceFactors} @ {confluence.ZonePrice:0.#####} [{string.Join("+", confluence.Factors)}]");
        sb.AppendLine($"       {rsiLabel}  |  ADX {adxValue:0.0}  |  ATR(M5) {plan.AtrPips:0.##}p  |  ATR(M15) {ctx.M15Atr.Result.Last(ctx.M15ClosedIndex) / ctx.Symbol.PipSize:0.##}p");
        sb.AppendLine($"       StochRSI %K={stoch.K:0.0} %D={stoch.D:0.0} slow%D={stoch.SlowD:0.0}  |  ticks {volumeRatio:0.00}x  |  spread {spreadPips:0.0}p  |  VWAP {ctx.Vwap:0.#####} (sd {ctx.VwapSd:0.#####})");
        sb.AppendLine($"       signal bars: H1 {ctx.H1.OpenTimes.Last(ctx.H1ClosedIndex):yyyy-MM-dd HH:mm}  M15 {ctx.M15.OpenTimes.Last(ctx.M15ClosedIndex):yyyy-MM-dd HH:mm}  M5 {ctx.M5.OpenTimes.Last(ctx.M5ClosedIndex):yyyy-MM-dd HH:mm}  |  H1 ema20/50 {ctx.H1EmaFast.Result.Last(ctx.H1ClosedIndex):0.#####}/{ctx.H1EmaSlow.Result.Last(ctx.H1ClosedIndex):0.#####}");
        Print(sb.ToString().TrimEnd());

        ctx.Entries++;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POSITION MANAGEMENT
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called from OnTick. Takes the TP1 partial close at the first opportunity,
    /// then moves the stop to breakeven plus a buffer and ratchets an ATR trail.
    /// Every branch compares the target against the live stop first so a
    /// no-op ModifyPosition is never sent.
    /// </summary>
    private void ManageOpenPosition(SymbolContext ctx, Position position, PositionState state)
    {
        if (!state.BreakEvenDone && !state.Tp1Done)
        {
            var price = state.TradeType == TradeType.Buy ? ctx.Symbol.Bid : ctx.Symbol.Ask;
            var tp1Price = state.TradeType == TradeType.Buy
                ? state.EntryPrice + state.Tp1Pips * ctx.Symbol.PipSize
                : state.EntryPrice - state.Tp1Pips * ctx.Symbol.PipSize;

            if ((state.TradeType == TradeType.Buy && price >= tp1Price) ||
                (state.TradeType == TradeType.Sell && price <= tp1Price))
            {
                TakePartialAtTp1(ctx, position, state);
                return;
            }
        }

        if (state.Tp1Done && !state.BreakEvenDone)
        {
            MoveToBreakeven(ctx, position, state);
            return;
        }

        if (state.Tp1Done && state.BreakEvenDone && UseTrailingStop)
            TrailStop(ctx, position, state);
    }

    /// <summary>
    /// Closes Tp1CloseFraction of the ticket at TP1. Volume in units, normalised
    /// DOWN to the symbol step, and never the whole ticket - the remaining half
    /// has to survive to TP2.
    /// </summary>
    private void TakePartialAtTp1(SymbolContext ctx, Position position, PositionState state)
    {
        var symbol = ctx.Symbol;
        var closeVolume = position.VolumeInUnits * Tp1CloseFraction;
        closeVolume = symbol.NormalizeVolumeInUnits(closeVolume, RoundingMode.Down);

        var remaining = position.VolumeInUnits - closeVolume;

        if (closeVolume < symbol.VolumeInUnitsMin || remaining < symbol.VolumeInUnitsMin)
        {
            // Not splittable at this size. Take the whole ticket at TP1 rather than
            // leaving a stub that cannot be managed.
            var full = ClosePosition(position);
            if (!full.IsSuccessful)
            {
                Print($"TP1 full close FAILED {ctx.Name}: {full.Error}");
                return;
            }

            Print($"TP1 {ctx.Name} {position.TradeType} closed in full at {position.CurrentPrice:0.#####} ({full.Position.Pips:0.##}p) - position too small to split.");
            state.Tp1Done = true;
            return;
        }

        var result = ClosePosition(position, closeVolume);
        if (!result.IsSuccessful)
        {
            Print($"TP1 partial close FAILED {ctx.Name} {position.TradeType} vol={closeVolume:0}: {result.Error}");
            return;
        }

        CreditPartialClose(state, closeVolume, result);
        state.Tp1Done = true;
        Print($"TP1 {ctx.Name} {position.TradeType} closed {closeVolume:0}/{position.VolumeInUnits:0} units at {result.Position.CurrentPrice:0.#####} ({result.Position.Pips:0.##}p). {remaining:0} units ride to TP2.");
    }

    /// <summary>
    /// Books a PARTIAL close into the daily tally. A partial close shrinks the
    /// ticket without closing it, so cTrader raises no OnPositionClosed for it -
    /// counting only whole positions silently drops every realized TP1 profit and
    /// makes the daily report disagree with the account by the size of TP1.
    /// Pips are weighted by the share of the ticket they closed, so the tally ends
    /// up as the position's true volume-weighted average rather than the last
    /// leg's raw distance.
    /// </summary>
    private void CreditPartialClose(PositionState state, double closedVolume, TradeResult result)
    {
        var fraction = state.OriginalVolume > 0
            ? Math.Min(1.0, closedVolume / state.OriginalVolume)
            : 0.0;

        state.RealizedProfit += result.Position.NetProfit;
        state.RealizedPips += result.Position.Pips * fraction;
        state.ClosedFraction += fraction;

        _netProfitToday += result.Position.NetProfit;
        _netPipsToday += result.Position.Pips * fraction;
    }

    /// <summary>
    /// Account-currency value of one pip for a given volume. Needed to price the
    /// stop in money so R can be measured against realised P&amp;L.
    /// cTrader 1.0.21 reports Symbol.TickValue for ONE UNIT of the base currency,
    /// not per lot, so the volume is applied in full. Dividing by the 100k lot
    /// size here is what put every R in the log six orders of magnitude out.
    /// </summary>
    private static double MoneyPerPip(Symbol symbol, double volumeInUnits)
    {
        if (symbol.TickSize <= 0 || volumeInUnits <= 0)
            return 0.0;

        var ticksPerPip = symbol.PipSize / symbol.TickSize;
        if (ticksPerPip <= 0)
            return 0.0;

        return symbol.TickValue * volumeInUnits * ticksPerPip;
    }

    /// <summary>
    /// Moves the stop to entry plus BreakEvenAtrBuffer x ATR, on the side that
    /// locks in the spread and commission rather than exactly at entry.
    /// </summary>
    private void MoveToBreakeven(SymbolContext ctx, Position position, PositionState state)
    {
        var symbol = ctx.Symbol;
        var buffer = state.AtrPips * BreakEvenAtrBuffer * symbol.PipSize;

        var target = state.TradeType == TradeType.Buy
            ? state.EntryPrice + buffer
            : state.EntryPrice - buffer;

        target = Math.Round(target, symbol.Digits);

        if (position.StopLoss.HasValue)
        {
            var current = position.StopLoss.Value;
            if (state.TradeType == TradeType.Buy && current >= target)
            {
                state.BreakEvenDone = true;
                return;
            }

            if (state.TradeType == TradeType.Sell && current <= target)
            {
                state.BreakEvenDone = true;
                return;
            }
        }

        if (!RespectsStopLevel(symbol, target, symbol.Bid, symbol.Ask))
            return;

        var result = ModifyPosition(position, target, position.TakeProfit, ProtectionType.Absolute);
        if (!result.IsSuccessful)
        {
            Print($"Breakeven FAILED {ctx.Name} {position.TradeType} target={target:0.#####}: {result.Error}");
            return;
        }

        state.BreakEvenDone = true;
        Print($"Breakeven set {ctx.Name} {position.TradeType} SL -> {target:0.#####}");
    }

    /// <summary>
    /// ATR trailing stop, active only after TP1. Ratchets one way only: the stop
    /// is never loosened, and a ModifyPosition is sent only when the target has
    /// actually moved.
    /// </summary>
    private void TrailStop(SymbolContext ctx, Position position, PositionState state)
    {
        var symbol = ctx.Symbol;
        var trailDistance = state.AtrPips * TrailAtrMultiplier * symbol.PipSize;
        if (trailDistance <= 0)
            return;

        var target = state.TradeType == TradeType.Buy
            ? symbol.Bid - trailDistance
            : symbol.Ask + trailDistance;

        target = Math.Round(target, symbol.Digits);

        if (position.StopLoss.HasValue)
        {
            var current = position.StopLoss.Value;

            // A raw "is it higher?" test re-sends ModifyPosition on every tick.
            // On a 2.0xATR trail the stop only ever creeps a pip or two at a
            // time, so one runner produced ~40 modify requests in 26 minutes -
            // pure broker round trips for a stop that is functionally unchanged.
            // Requiring a real step keeps the trail identical in substance while
            // cutting the request count by an order of magnitude.
            var minStep = state.AtrPips * TrailMinStepAtrShare * symbol.PipSize;
            if (minStep <= 0)
                minStep = symbol.PipSize;

            if (state.TradeType == TradeType.Buy && target < current + minStep)
                return;
            if (state.TradeType == TradeType.Sell && target > current - minStep)
                return;
        }
        else
        {
            // No stop at all - always an improvement.
            if (state.TradeType == TradeType.Buy && target >= symbol.Bid)
                return;
            if (state.TradeType == TradeType.Sell && target <= symbol.Ask)
                return;
        }

        if (!RespectsStopLevel(symbol, target, symbol.Bid, symbol.Ask))
            return;

        var result = ModifyPosition(position, target, position.TakeProfit, ProtectionType.Absolute);
        if (!result.IsSuccessful)
        {
            Print($"Trail FAILED {ctx.Name} {position.TradeType} target={target:0.#####}: {result.Error}");
            return;
        }

        if (VerboseLogging)
            Print($"Trail {ctx.Name} {position.TradeType} SL -> {target:0.#####}");
    }

    /// <summary>
    /// Flattens any open position whose symbol is about to stop trading. This
    /// covers the Friday weekly close, holiday early-closes, and any broker whose
    /// close comes before the 16:30 UTC session end.
    ///
    /// The trigger is the symbol's own reported <see cref="MarketHours.TimeTillClose"/>,
    /// not a hardcoded server offset, so it is correct whatever the broker's
    /// timezone is and whether or not it observes DST.
    /// </summary>
    private void FlattenIfMarketClosing()
    {
        if (CloseBeforeMarketCloseMinutes <= 0)
            return;

        var threshold = TimeSpan.FromMinutes(CloseBeforeMarketCloseMinutes);
        var open = Positions.FindAll(Label);

        for (var i = 0; i < open.Length; i++)
        {
            var pos = open[i];
            var symbol = Symbols.GetSymbol(pos.SymbolName);

            if (symbol == null || !symbol.MarketHours.IsOpened())
                continue;

            var tillClose = symbol.MarketHours.TimeTillClose();
            if (tillClose > threshold)
                continue;

            var result = ClosePosition(pos);

            if (result.IsSuccessful)
                Print($"FLATTEN (market closes in {tillClose.TotalMinutes:0} min) {pos.SymbolName} {pos.TradeType} closed at {result.Position.CurrentPrice:0.#####} ({result.Position.Pips:0.##}p)");
            else
                Print($"FLATTEN FAILED (market close) {pos.SymbolName} {pos.TradeType} id={pos.Id}: {result.Error}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FLATTENING
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brokers reject a protective order sitting closer to price than their
    /// minimum stop distance with InvalidStopLossTakeProfit. Right after TP1 the
    /// trailing target can be inside that band, so the modify is skipped (and
    /// retried on a later tick) rather than sent as a guaranteed rejection every
    /// tick. MinStopLossDistance is expressed in pips.
    /// </summary>
    private static bool RespectsStopLevel(Symbol symbol, double stopPrice, double bid, double ask)
    {
        var minDistance = symbol.MinStopLossDistance * symbol.PipSize;
        if (minDistance <= 0)
            return true;

        return stopPrice <= bid - minDistance || stopPrice >= ask + minDistance;
    }

    /// <summary>
    /// Closes every position carrying this label, at market, with a checked
    /// result per close. The collection is snapshotted first because closing
    /// mutates it. Idempotent: safe to call on every bar after the session end.
    /// </summary>
    private void CloseAllPositions(string reason)
    {
        FlattenIfMarketClosing();

        var open = Positions.FindAll(Label);
        if (open.Length == 0)
        {
            // Nothing left to flatten, but the day still owes its report. This is
            // the normal path when the cBot was started after 16:30 UTC or when
            // every position had already been stopped out.
            FlushSessionSummary(reason);
            return;
        }

        for (var i = 0; i < open.Length; i++)
        {
            var pos = open[i];
            var result = ClosePosition(pos);

            if (result.IsSuccessful)
                Print($"FLATTEN ({reason}) {pos.SymbolName} {pos.TradeType} closed at {result.Position.CurrentPrice:0.#####} ({result.Position.Pips:0.##}p, {result.Position.NetProfit:0.00} {Account.Asset.Name})");
            else
                Print($"FLATTEN FAILED ({reason}) {pos.SymbolName} {pos.TradeType} id={pos.Id}: {result.Error}");
        }

        FlushSessionSummary(reason);
    }

    /// <summary>Prints the end-of-session summary exactly once per day.</summary>
    private void FlushSessionSummary(string reason)
    {
        var today = TimeInUtc.Date;
        if (_eodFlushedOn == today)
            return;

        _eodFlushedOn = today;
        PrintSessionSummary(reason);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RESTART RECONCILIATION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds management state for positions opened before a restart. The ATR
    /// and R distances are read back from the position comment written at entry.
    /// A position with a missing comment or a missing stop is logged loudly -
    /// silently leaving it unmanaged is the failure this method exists to prevent.
    /// </summary>
    private void ReconcileOpenPositions()
    {
        var open = Positions.FindAll(Label);
        if (open.Length == 0)
            return;

        Print($"Reconcile: {open.Length} position(s) open from a previous run.");

        for (var i = 0; i < open.Length; i++)
        {
            var pos = open[i];

            if (!TryReadComment(pos.Comment, out var originalVolume, out var atrPips, out var riskPips, out var tp1Pips, out var tp2Pips))
            {
                Print($"WARN: cannot rebuild management state for {pos.SymbolName} id={pos.Id} (comment '{pos.Comment}'). " +
                      "It will still be flattened at session end, but no TP1/breakeven/trail will run. Close it manually if that is not acceptable.");
                continue;
            }

            _positionStates[pos.Id] = new PositionState
            {
                Id = pos.Id,
                SymbolName = pos.SymbolName,
                TradeType = pos.TradeType,
                EntryPrice = pos.EntryPrice,
                AtrPips = atrPips,
                RiskPips = riskPips,
                Tp1Pips = tp1Pips,
                Tp2Pips = tp2Pips,
                OriginalVolume = originalVolume,
                // Exact: the comment carries the volume the entry was opened with,
                // so a smaller live volume can only mean the TP1 partial was taken.
                // Guessing from the stop or from VolumeInUnitsMax was unreliable.
                Tp1Done = originalVolume > 0 && pos.VolumeInUnits < originalVolume,
                BreakEvenDone = pos.StopLoss.HasValue && IsAtOrPastBreakeven(pos)
            };

            if (!pos.StopLoss.HasValue)
                Print($"WARN: {pos.SymbolName} id={pos.Id} has NO stop loss. Re-applying from the comment ATR.");

            Print($"Reconcile: {pos.SymbolName} {pos.TradeType} entry={pos.EntryPrice:0.#####} atr={atrPips:0.##}p r={riskPips:0.##}p tp1={tp1Pips:0.##}p tp2={tp2Pips:0.##}p tp1Done={_positionStates[pos.Id].Tp1Done}");
        }
    }

    private static bool IsAtOrPastBreakeven(Position pos)
    {
        if (!pos.StopLoss.HasValue)
            return false;

        return pos.TradeType == TradeType.Buy
            ? pos.StopLoss.Value >= pos.EntryPrice
            : pos.StopLoss.Value <= pos.EntryPrice;
    }

    /// <summary>
    /// Parses the entry comment written by <see cref="OpenPosition"/>.
    /// Returns false on any malformed input rather than guessing.
    /// </summary>
    private static bool TryReadComment(string comment, out double originalVolume, out double atrPips, out double riskPips, out double tp1Pips, out double tp2Pips)
    {
        originalVolume = atrPips = riskPips = tp1Pips = tp2Pips = 0;

        if (string.IsNullOrEmpty(comment))
            return false;

        var parts = comment.Split('|');
        if (parts.Length < 5 || parts[0] != CommentPrefix)
            return false;

        for (var i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split(new[] { '=' }, 2);
            if (kv.Length != 2)
                continue;

            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;

            switch (kv[0])
            {
                case "vol":
                    originalVolume = value;
                    break;
                case "atr":
                    atrPips = value;
                    break;
                case "r":
                    riskPips = value;
                    break;
                case "tp1":
                    tp1Pips = value;
                    break;
                case "tp2":
                    tp2Pips = value;
                    break;
            }
        }

        return atrPips > 0 && riskPips > 0 && tp1Pips > 0 && tp2Pips > 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POSITION EVENTS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires for every close on the account, including manual closes and other
    /// algos' closes - so the label check is what keeps the daily statistics
    /// about this cBot only.
    /// </summary>
    [Obsolete("Only OnPositionClosed(Position) exists in cAlgo.API 1.0.21.")]
    protected override void OnPositionClosed(Position position)
    {
        if (position.Label != Label)
            return;

        // Read the state before dropping it. position.NetProfit covers only the
        // volume still open when the event fires, so a ticket that already took
        // its TP1 slice has that slice booked and only the remainder is left here.
        _positionStates.TryGetValue(position.Id, out var state);

        if (state != null)
        {
            var remainingFraction = Math.Max(0.0, 1.0 - state.ClosedFraction);

            _netProfitToday += position.NetProfit;
            _netPipsToday += position.Pips * remainingFraction;

            // Win/loss belongs to the whole position. Judging it on the last leg
            // alone files a trade that banked TP1 and then trailed out as a loss.
            var totalProfit = state.RealizedProfit + position.NetProfit;
            var totalPips = state.RealizedPips + position.Pips * remainingFraction;

            // R is measured in money, not in pips. Position.Pips is a raw price
            // distance that excludes spread and commission, so a pip-based R reads
            // ~1.00 on a clean stop-out while the account is actually down ~1.2R.
            // Dividing realised money by the money the stop was worth is the only
            // figure that can be summed into a real expectancy.
            var closedSymbol = Symbols.GetSymbol(position.SymbolName);
            var perPip = closedSymbol != null ? MoneyPerPip(closedSymbol, state.OriginalVolume) : 0.0;
            var riskMoney = perPip * state.RiskPips;
            var moneyR = riskMoney > 0 ? totalProfit / riskMoney : 0.0;

            if (totalProfit >= 0)
                _winsToday++;
            else
                _lossesToday++;

            _rSumToday += moneyR;
            _rCountToday++;

            var rText = riskMoney > 0
                ? $"{moneyR:+0.00;-0.00;0.00}R | {totalProfit:0.00}/{riskMoney:0.00} risk @ {perPip:0.0000}/pip"
                : $"R n/a | {perPip:0.0000}/pip";
            var legNote = state.RealizedProfit != 0.0
                ? $"tp1 {state.RealizedProfit:0.00} + final {position.NetProfit:0.00}"
                : $"final {position.NetProfit:0.00}";

            Print($"CLOSED {position.SymbolName} {position.TradeType} {totalPips:0.##}p net  {totalProfit:0.00} {Account.Asset.Name}  " +
                  $"({rText} | stop {state.RiskPips:0.##}p | {legNote})  " +
                  $"(day: {_winsToday}W/{_lossesToday}L, {_netPipsToday:0.##}p, {_netProfitToday:0.00} {Account.Asset.Name})");
        }
        else
        {
            // State was lost - a restart between the fill and the close, or a
            // position this instance never built. Count the event so the day
            // still reconciles, but say so rather than implying it is exact.
            _netPipsToday += position.Pips;
            _netProfitToday += position.NetProfit;

            if (position.NetProfit >= 0)
                _winsToday++;
            else
                _lossesToday++;

            Print($"CLOSED {position.SymbolName} {position.TradeType} {position.Pips:0.##}p  {position.NetProfit:0.00} {Account.Asset.Name}  " +
                  $"(NO STATE - any TP1 slice is uncounted, no R)  " +
                  $"(day: {_winsToday}W/{_lossesToday}L, {_netPipsToday:0.##}p, {_netProfitToday:0.00} {Account.Asset.Name})");
        }

        _positionStates.Remove(position.Id);

        CheckDailyLossLimit();
    }

    /// <summary>
    /// Fires for every open on the account. Restores state for a position this
    /// cBot owns that OnStart did not see, which happens if the cBot attaches
    /// while a position from an earlier instance is still live.
    /// </summary>
    [Obsolete("Only OnPositionOpened(Position) exists in cAlgo.API 1.0.21.")]
    protected override void OnPositionOpened(Position position)
    {
        if (position.Label != Label)
            return;

        if (_positionStates.ContainsKey(position.Id))
            return;

        if (TryReadComment(position.Comment, out var originalVolume, out var atrPips, out var riskPips, out var tp1Pips, out var tp2Pips))
        {
            _positionStates[position.Id] = new PositionState
            {
                Id = position.Id,
                SymbolName = position.SymbolName,
                TradeType = position.TradeType,
                EntryPrice = position.EntryPrice,
                AtrPips = atrPips,
                RiskPips = riskPips,
                Tp1Pips = tp1Pips,
                Tp2Pips = tp2Pips,
                OriginalVolume = originalVolume,
                Tp1Done = originalVolume > 0 && position.VolumeInUnits < originalVolume,
                BreakEvenDone = position.StopLoss.HasValue && IsAtOrPastBreakeven(position)
            };
        }
    }
}
