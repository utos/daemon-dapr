using Execution.Infrastructure.Dapr;

namespace UnitTests.Dapr;

public class DaprTimerDeadlineTests
{
    private static readonly DateTime Noon = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_deadline_already_on_a_whole_second_is_left_alone()
    {
        var deadline = DaprTimerDeadline.WholeSecond(Noon, TimeSpan.FromSeconds(30));

        Assert.Equal(Noon.AddSeconds(30), deadline);
    }

    [Fact]
    public void A_sub_second_remainder_rounds_up()
    {
        // Dapr truncates the fire time to a whole second, so 12:00:30.416 would fire at
        // 12:00:30.000 — 416ms before the wait was up.
        var from = Noon.AddMilliseconds(416);

        var deadline = DaprTimerDeadline.WholeSecond(from, TimeSpan.FromSeconds(30));

        Assert.Equal(Noon.AddSeconds(31), deadline);
    }

    [Fact]
    public void The_truncated_fire_time_never_lands_before_the_requested_delay()
    {
        var delay = TimeSpan.FromMilliseconds(700);

        for (var offsetMs = 0; offsetMs < 1000; offsetMs += 37)
        {
            var from = Noon.AddMilliseconds(offsetMs);
            var deadline = DaprTimerDeadline.WholeSecond(from, delay);

            // What Dapr will actually do with the deadline we hand it.
            var fired = new DateTime(
                deadline.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, deadline.Kind);

            Assert.True(
                fired - from >= delay,
                $"a {delay.TotalMilliseconds}ms timer started at +{offsetMs}ms resumed after " +
                $"{(fired - from).TotalMilliseconds}ms");
        }
    }

    [Fact]
    public void Rounding_up_never_overshoots_by_a_whole_second_or_more()
    {
        var delay = TimeSpan.FromMilliseconds(700);

        for (var offsetMs = 0; offsetMs < 1000; offsetMs += 37)
        {
            var from = Noon.AddMilliseconds(offsetMs);
            var deadline = DaprTimerDeadline.WholeSecond(from, delay);

            Assert.True(deadline - (from + delay) < TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void The_kind_is_preserved_so_the_deadline_stays_utc()
    {
        var deadline = DaprTimerDeadline.WholeSecond(Noon.AddMilliseconds(1), TimeSpan.FromSeconds(1));

        Assert.Equal(DateTimeKind.Utc, deadline.Kind);
    }
}
