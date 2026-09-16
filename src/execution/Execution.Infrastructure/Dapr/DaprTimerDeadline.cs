namespace Execution.Infrastructure.Dapr;

/// <summary>
/// Compensates for Dapr's timer granularity. Lives with the driver rather than in the
/// domain: the domain says "wait this long", and how long a wait has to be *asked* for to
/// actually last that long is a property of this runtime.
/// </summary>
public static class DaprTimerDeadline
{
    /// <summary>
    /// The instant a timer of <paramref name="delay"/> started at <paramref name="from"/> may
    /// resume, rounded up to a whole second.
    ///
    /// Dapr truncates a timer's fire time to whole seconds, so a deadline of 12:00:30.416
    /// fires at 12:00:30.000 — up to 999ms early. That would make "wait 1 second" wait less
    /// than a second, which is the one thing a delay must not do. Rounding the deadline up
    /// means the truncated fire time always lands at or after the requested delay; the cost
    /// is waiting up to a second longer, which is the safe direction to err.
    ///
    /// Measured over 14 samples: none resumed early, and the overshoot ranged 109-989ms. So a
    /// 700ms timer waited 1.6s. Below roughly a second the wait is dominated by this rounding
    /// rather than by the requested duration — sub-second precision is not achievable against
    /// a one-second granularity, and a delay that is too short is the worse failure.
    ///
    /// Note when measuring this: elapsed time across an orchestrator await must come from
    /// <c>WorkflowContext.CurrentUtcDateTime</c>. <c>DateTime.UtcNow</c> reads the same instant
    /// on both sides, because the code either side of the await runs back-to-back on replay.
    /// </summary>
    public static DateTime WholeSecond(DateTime from, TimeSpan delay)
    {
        var deadline = from + delay;

        if (deadline.Ticks % TimeSpan.TicksPerSecond == 0)
            return deadline;

        var roundedUp = (deadline.Ticks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond;
        return new DateTime(roundedUp, deadline.Kind);
    }
}
