using Common.SeedWork;
using Observability.Application.Queries;

namespace UnitTests.Observability;

/// <summary>
/// What a watching client reads on each line. The daemon's log calls end in
/// <c>(execution {id})</c> so the host's interleaved logs stay attributable, and a watch stream is
/// already scoped to one execution — repeating its id there is noise on every line.
/// </summary>
public class WatchExecutionMessageTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ExecutionId Child = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void Drops_the_suffix_naming_this_execution() =>
        Assert.Equal(
            "Activity 'assess' completed in 842ms",
            WatchExecutionHandler.WithoutExecutionSuffix(
                $"Activity 'assess' completed in 842ms (execution {Id})", Id));

    [Fact]
    public void Keeps_a_suffix_naming_a_different_execution()
    {
        // A different id is information — a child this run started, say — not repetition.
        var message = $"Activity 'settle' started (execution {Child})";

        Assert.Equal(message, WatchExecutionHandler.WithoutExecutionSuffix(message, Id));
    }

    [Fact]
    public void Leaves_an_id_mentioned_mid_message_alone()
    {
        var message = $"Execution {Id} is already cancelled; nothing to do";

        Assert.Equal(message, WatchExecutionHandler.WithoutExecutionSuffix(message, Id));
    }

    [Fact]
    public void Passes_a_message_without_a_suffix_through_unchanged() =>
        Assert.Equal("Execution completed", WatchExecutionHandler.WithoutExecutionSuffix("Execution completed", Id));
}
