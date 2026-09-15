using System.Collections.Concurrent;
using Common.SeedWork;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Observability.Domain;

namespace Observability.Infrastructure.Store;

public class ExecutionLogStore : IExecutionLogStore
{
    private readonly ConcurrentDictionary<ExecutionId, ExecutionLogBuffer> _buffers = new();

    public void AddEntry(ExecutionId executionId, ExecutionLogEntry entry)
        => _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer()).Add(new LogItem(entry));

    public void Complete(ExecutionId executionId)
    {
        _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer()).Complete();
        ScheduleEviction(executionId);
    }

    public void CompleteWith(
        ExecutionId executionId, DateTime timestamp, ExecutionOutcome outcome, string? failureReason,
        string? failureCode = null)
    {
        var buffer = _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer());
        // The terminal frame must land in the stream before it closes, so add then complete.
        buffer.Add(new TerminalItem(timestamp, outcome, failureReason, failureCode));
        buffer.Complete();
        ScheduleEviction(executionId);
    }

    private void ScheduleEviction(ExecutionId executionId)
        => _ = Task.Delay(TimeSpan.FromMinutes(5))
                   .ContinueWith(t => _buffers.TryRemove(executionId, out _), TaskScheduler.Default);

    public async IAsyncEnumerable<ExecutionStreamItem> SubscribeAsync(
        ExecutionId executionId, int? after, int? tail, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer());
        var (history, channel) = buffer.Subscribe();

        int skip;
        if (after.HasValue)
            skip = after.Value + 1;
        else if (tail.HasValue)
            skip = Math.Max(0, history.Count - tail.Value);
        else
            skip = 0;

        for (var i = skip; i < history.Count; i++)
            yield return history[i];

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;
    }

    private sealed class ExecutionLogBuffer
    {
        private readonly object _lock = new();
        private readonly List<ExecutionStreamItem> _history = [];
        private readonly List<Channel<ExecutionStreamItem>> _subscribers = [];
        private bool _completed;

        public void Add(ExecutionStreamItem item)
        {
            lock (_lock)
            {
                _history.Add(item);
                foreach (var ch in _subscribers)
                    ch.Writer.TryWrite(item);
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
                foreach (var ch in _subscribers)
                    ch.Writer.TryComplete();
                _subscribers.Clear();
            }
        }

        public (IReadOnlyList<ExecutionStreamItem> history, Channel<ExecutionStreamItem> channel) Subscribe()
        {
            lock (_lock)
            {
                var channel = Channel.CreateUnbounded<ExecutionStreamItem>();
                if (_completed)
                    channel.Writer.TryComplete();
                else
                    _subscribers.Add(channel);
                return (_history.ToList(), channel);
            }
        }
    }
}
