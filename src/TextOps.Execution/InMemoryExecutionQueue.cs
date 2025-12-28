using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TextOps.Contracts.Execution;

namespace TextOps.Execution;

/// <summary>
/// In-memory execution queue for development and testing.
/// Uses System.Threading.Channels for the queue and ConcurrentDictionary for claim tracking.
/// Does not persist across restarts. Use DatabaseExecutionQueue for production.
/// </summary>
public sealed class InMemoryExecutionQueue : IExecutionQueue
{
    private readonly Channel<QueuedDispatch> _channel;
    private readonly ConcurrentDictionary<long, QueuedDispatch> _processing = new();
    private long _nextId;

    public InMemoryExecutionQueue()
    {
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<QueuedDispatch>(options);
    }

    public void Enqueue(ExecutionDispatch dispatch)
    {
        var id = Interlocked.Increment(ref _nextId);
        var queued = new QueuedDispatch(id, dispatch.RunId, dispatch.JobKey, Attempts: 1);
        _channel.Writer.TryWrite(queued);
    }

    public async Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken ct = default)
    {
        try
        {
            if (await _channel.Reader.WaitToReadAsync(ct))
            {
                if (_channel.Reader.TryRead(out var queued))
                {
                    _processing[queued.QueueId] = queued;
                    return queued;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        return null;
    }

    public Task CompleteAsync(long queueId, bool success, string? error, CancellationToken ct = default)
    {
        _processing.TryRemove(queueId, out _);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(long queueId, string? error, CancellationToken ct = default)
    {
        if (_processing.TryRemove(queueId, out var queued))
        {
            // Re-queue with incremented attempt count
            var requeued = queued with { Attempts = queued.Attempts + 1 };
            _channel.Writer.TryWrite(requeued);
        }
        return Task.CompletedTask;
    }

    public Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken ct = default)
    {
        // In-memory queue doesn't have stale locks (entries are either in channel or processing dict)
        return Task.FromResult(0);
    }
}
