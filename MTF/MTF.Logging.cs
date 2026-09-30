using System;
using System.Globalization;
using System.Text;

namespace cAlgo.Robots;

// ─────────────────────────────────────────────────────────────────────────────
// MTF.Logging — reject accounting and the daily report.
//
// A cBot scanning five symbols on M5 has 1,440 evaluation opportunities per day.
// Printing every rejection unconditionally would bury the log (and the code-review
// rule against per-event Print spam), so:
//
//   * VerboseLogging ON  -> every rejection prints, with its blocking reason.
//   * VerboseLogging OFF -> one consolidated line per symbol per bar-close, only
//                           when that symbol produced a NEAR MISS (at least one
//                           confluence layer passed), plus a running tally.
// ─────────────────────────────────────────────────────────────────────────────
public partial class MTF
{
    private const int NearMissCarry = 3;

    /// <summary>
    /// True when the rejection happened at or beyond the M15 / M5 layers, i.e. the
    /// setup was real and only the score fell short. Those are the cases worth a
    /// consolidated line. Routine gates (no H1 direction, no VWAP yet, spread,
    /// trade budget, daily loss) are not near misses and are not reported.
    /// </summary>
    private static bool IsNearMissReason(string reason)
    {
        return reason.StartsWith("confluence", StringComparison.Ordinal)
               || reason.StartsWith("score", StringComparison.Ordinal)
               || reason.StartsWith("no M15", StringComparison.Ordinal)
               || reason.StartsWith("no Stoch", StringComparison.Ordinal)
               || reason.StartsWith("no Bullish", StringComparison.Ordinal)
               || reason.StartsWith("no Bearish", StringComparison.Ordinal)
               || reason.StartsWith("candle", StringComparison.Ordinal)
               || reason.StartsWith("risk", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records a rejected signal. With verbose logging on this prints the reason
    /// immediately; otherwise it accumulates a per-symbol near-miss summary that
    /// <see cref="FlushNearMiss"/> emits once the symbol has been the closest
    /// candidate for <see cref="NearMissCarry"/> consecutive bars.
    /// </summary>
    private void Reject(SymbolContext ctx, string reason)
    {
        if (VerboseLogging)
        {
            Print($"REJECT {ctx.Name} @ {TimeInUtc:yyyy-MM-dd HH:mm} UTC - {reason}");
            return;
        }

        if (IsNearMissReason(reason))
        {
            ctx.NearMissStreak++;
            ctx.NearMissDetail = reason;
            return;
        }

        ctx.NearMissStreak = 0;
        ctx.NearMissDetail = null;
    }

    /// <summary>
    /// Emits the consolidated near-miss line when a symbol has been the closest
    /// candidate for several consecutive bars. Called exactly once per symbol per
    /// bar from the scan loop, so the streak can never leak across symbols.
    /// </summary>
    private void FlushNearMiss(SymbolContext ctx)
    {
        if (VerboseLogging || ctx.NearMissStreak < NearMissCarry)
            return;

        Print($"NEAR MISS {ctx.Name} @ {TimeInUtc:yyyy-MM-dd HH:mm} UTC x{ctx.NearMissStreak} | " +
              $"bias={DescribeBias(ctx.LastBias)} adx={ctx.LastAdx:0.0} | {ctx.LastZoneSummary} | " +
              $"blocked by: {ctx.NearMissDetail}");

        ctx.NearMissStreak = 0;
        ctx.NearMissDetail = null;
    }

    /// <summary>Clears near-miss accounting after a symbol actually trades.</summary>
    private void ResetNearMiss(SymbolContext ctx)
    {
        ctx.NearMissStreak = 0;
        ctx.NearMissDetail = null;
    }

    private static string DescribeBias(Bias bias)
        => bias == Bias.None ? "none" : bias.ToString().ToLowerInvariant();

    /// <summary>
    /// The daily report: trade count, win rate, and net P&amp;L in both pips and
    /// account currency. Called on the session-end flatten, on a day rollover, and
    /// again in OnStop so a terminal closed mid-session still reports.
    /// </summary>
    private void PrintSessionSummary(string reason)
    {
        if (_stopped && _eodFlushedOn == TimeInUtc.Date)
            return;

        if (_trackedDate == DateTime.MinValue)
            return;

        var closed = _winsToday + _lossesToday;
        var winRate = closed > 0 ? 100.0 * _winsToday / closed : 0.0;

        // The balance delta is the realized P&L of the day and is correct no matter
        // which close events have been dispatched yet. The tracked figure is the
        // event tally, so printing the two side by side turns any future
        // double-count or missed partial into a visible line instead of a silent
        // wrong number - the session-end flatten in particular can report before
        // its own OnPositionClosed has run.
        var realizedNet = Account.Balance - _dayStartBalance;
        var drift = _netProfitToday - realizedNet;
        var driftNote = Math.Abs(drift) <= 0.01
            ? "reconciled"
            : $"{drift:+0.00;-0.00;0.00} off (event timing)";

        var sb = new StringBuilder();
        sb.AppendLine($"── MTF DAILY SUMMARY ({reason}) {_trackedDate:yyyy-MM-dd} ──");
        sb.AppendLine($"  trades entered : {_tradesToday} / {MaxTradesPerDay}");
        sb.AppendLine($"  closed         : {_winsToday}W / {_lossesToday}L   win rate {winRate:0.0}%");
        sb.AppendLine($"  net pips       : {_netPipsToday:0.##}   (volume-weighted)");
        sb.AppendLine($"  net profit     : {realizedNet:0.00} {Account.Asset.Name}   realized, balance delta");
        sb.AppendLine($"  event tally    : {_netProfitToday:0.00} {Account.Asset.Name}   {driftNote}");
        sb.AppendLine($"  expectancy     : {(_rCountToday > 0 ? _rSumToday / _rCountToday : 0.0):+0.000;-0.000;0.000} R/trade over {_rCountToday} closed   (total {_rSumToday:+0.00;-0.00;0.00}R, money-based)");
        sb.AppendLine($"  day equity     : start {_dayStartEquity:0.00} -> now {Account.Equity:0.00} {Account.Asset.Name}  " +
                      $"({(_dayStartEquity - Account.Equity >= 0 ? "-" : "+")}{Math.Abs(_dayStartEquity - Account.Equity):0.00}, limit {DailyLossLimitPercent:0.##}%)");
        sb.AppendLine($"  daily breaker  : {(_dailyLossTripped ? "TRIPPED" : "clear")}");
        sb.AppendLine($"  still open     : {Positions.FindAll(Label).Length}");

        Print(sb.ToString().TrimEnd());
        PrintGateTelemetry(reason);
    }

    /// <summary>
    /// Measured pass rate of every gate, per symbol. Printed in the daily report and
    /// on stop so a backtest says which gate actually removed the setups instead of
    /// leaving it to be inferred from reject text. A gate with a very low rate is the
    /// one to loosen or drop; a gate at ~100% is costing nothing and can stay.
    /// </summary>
    private void PrintGateTelemetry(string title)
    {
        long evaluated = 0, aligned = 0, entries = 0;

        for (var i = 0; i < _contextArray.Length; i++)
        {
            var c = _contextArray[i];
            evaluated += c.Evaluations;
            aligned += c.Evaluations + c.AlignmentSkips;
            entries += c.Entries;
        }

        if (aligned == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"── MTF GATE TELEMETRY ({title}) ──");
        sb.AppendLine($"  bars offered    : {aligned}   evaluated {Rate(evaluated, aligned):0.0}%   alignment skips {aligned - evaluated}");

        for (var i = 0; i < _contextArray.Length; i++)
        {
            var c = _contextArray[i];
            if (c.Evaluations == 0)
                continue;

            // Each stage is a rate against the stage it actually receives, not
            // against total evaluations. Chained-of-evaluations rates all collapse
            // toward zero and hide which gate is actually binding.
            sb.AppendLine($"  {c.Name,-7} evals {c.Evaluations,6} | spread {Rate(c.PassSpread, c.Evaluations),5:0.0}% | " +
                          $"bias {Rate(c.PassBias, c.PassSpread),5:0.0}% | " +
                          $"confluence {Rate(c.PassConfluence, c.PassBias),5:0.0}% ({c.PassConfluence}) | " +
                          $"score>={MinSetupScore:0.0} {Rate(c.PassScore, c.PassConfluence),5:0.0}% ({c.PassScore}) | " +
                          $"risk {Rate(c.PassRisk, c.PassScore),5:0.0}% ({c.PassRisk}) | entries {c.Entries}");
        }

        sb.AppendLine($"  signal-to-entry : {(aligned == 0 ? 0d : (double)entries / aligned):0.0000} entries per bar offered");
        Print(sb.ToString().TrimEnd());
    }

    private static double Rate(long numerator, long denominator)
        => denominator <= 0 ? 0 : 100.0 * numerator / denominator;
}
