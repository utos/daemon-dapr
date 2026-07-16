using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Observability.Domain;

namespace Observability.Infrastructure.Store;

public class ExecutionLogStore : IExecutionLogStore
{
    private readonly ConcurrentDictionary<Guid, ExecutionLogBuffer> _buffers = new();

    public void AddEntry(Guid executionId, ExecutionLogEntry entry)
        => _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer()).Add(entry);

    public void Complete(Guid executionId)
    {
        _buffers.GetOrAdd(executionId, _ => new ExecutionLogBuffer()).Complete();
        _ = Task.Delay(TimeSpan.FromMinutes(5))
                .ContinueWith(t => _buffers.TryRemove(executionId, out _), TaskScheduler.Default);
    }

    public async IAsyncEnumerable<ExecutionLogEntry> SubscribeAsync(
        Guid executionId, int? after, int? tail, [EnumeratorCancellation] CancellationToken ct)
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

        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
            yield return entry;
    }

    private sealed class ExecutionLogBuffer
    {
        private readonly object _lock = new();
        private readonly List<ExecutionLogEntry> _history = [];
        private readonly List<Channel<ExecutionLogEntry>> _subscribers = [];
        private bool _completed;

        public void Add(ExecutionLogEntry entry)
        {
            lock (_lock)
            {
                _history.Add(entry);
                foreach (var ch in _subscribers)
                    ch.Writer.TryWrite(entry);
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

        public (IReadOnlyList<ExecutionLogEntry> history, Channel<ExecutionLogEntry> channel) Subscribe()
        {
            lock (_lock)
            {
                var channel = Channel.CreateUnbounded<ExecutionLogEntry>();
                if (_completed)
                    channel.Writer.TryComplete();
                else
                    _subscribers.Add(channel);
                return (_history.ToList(), channel);
            }
        }
    }
}
