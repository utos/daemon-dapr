using Grpc.Core;
using Utos.Daemon.V1;

namespace IntegrationTests.Shared;

/// <summary>
/// Extension methods for working with WatchExecution streaming responses.
/// Provides helpers for waiting on specific events with timeouts.
/// </summary>
public static class WatchExecutionResponseStreamExtensions
{
    /// <summary>
    /// Waits for any event matching a predicate.
    /// </summary>
    /// <param name="call">The streaming call</param>
    /// <param name="predicate">The condition to match</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="description">Description of the expected event for error messages</param>
    /// <returns>The first event matching the predicate</returns>
    /// <exception cref="TimeoutException">Thrown if no matching event is received within the timeout</exception>
    public static async Task<WatchExecutionResponse> WaitForEvent(
        this AsyncServerStreamingCall<WatchExecutionResponse> call,
        Func<WatchExecutionResponse, bool> predicate,
        TimeSpan timeout,
        string description = "expected event")
    {
        using var cts = new CancellationTokenSource(timeout);
        var reader = call.ResponseStream;

        while (await reader.MoveNext(cts.Token))
        {
            var current = reader.Current;

            if (predicate(current)) return current;
        }

        throw new TimeoutException($"{description} not received within {timeout}");
    }

    /// <summary>
    /// Collects a specific number of events from the stream.
    /// </summary>
    /// <param name="call">The streaming call</param>
    /// <param name="count">Number of events to collect</param>
    /// <param name="timeout">Maximum time to wait for all events</param>
    /// <returns>List of collected events</returns>
    /// <exception cref="TimeoutException">Thrown if not enough events are received within the timeout</exception>
    public static async Task<List<WatchExecutionResponse>> CollectEvents(
        this AsyncServerStreamingCall<WatchExecutionResponse> call,
        int count,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var reader = call.ResponseStream;
        var events = new List<WatchExecutionResponse>();

        while (events.Count < count && await reader.MoveNext(cts.Token)) events.Add(reader.Current);

        if (events.Count < count)
            throw new TimeoutException(
                $"Only received {events.Count} of {count} expected events within {timeout}");

        return events;
    }

    /// <summary>
    /// Collects all events until the server closes the stream.
    /// The stream closes automatically when the workflow reaches a terminal state
    /// when the execution log store signals completion on the terminal event.
    /// </summary>
    /// <param name="call">The streaming call</param>
    /// <param name="timeout">Maximum time to wait for the stream to close</param>
    /// <returns>List of all events received until the stream closed</returns>
    /// <exception cref="TimeoutException">Thrown if the stream does not close within the timeout</exception>
    public static async Task<List<WatchExecutionResponse>> CollectUntilCompletion(
        this AsyncServerStreamingCall<WatchExecutionResponse> call,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var reader = call.ResponseStream;
        var events = new List<WatchExecutionResponse>();

        while (await reader.MoveNext(cts.Token))
            events.Add(reader.Current);

        return events;
    }
}
