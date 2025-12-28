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

    public Task EnqueueAsync(ExecutionDispatch executionDispatch, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var queuedDispatch = new QueuedDispatch(id, executionDispatch.RunId, executionDispatch.JobKey, Attempts: 1);
        _channel.Writer.TryWrite(queuedDispatch);
        return Task.CompletedTask;
    }

    public async Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var queuedDispatch))
                {
                    _processing[queuedDispatch.QueueId] = queuedDispatch;
                    return queuedDispatch;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return null;
    }

    public Task CompleteAsync(long queueId, bool success, string? errorMessage, CancellationToken cancellationToken = default)
    {
        _processing.TryRemove(queueId, out _);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(long queueId, string? errorMessage, CancellationToken cancellationToken = default)
    {
        if (_processing.TryRemove(queueId, out var queuedDispatch))
        {
            var requeuedDispatch = queuedDispatch with { Attempts = queuedDispatch.Attempts + 1 };
            _channel.Writer.TryWrite(requeuedDispatch);
        }
        return Task.CompletedTask;
    }

    public Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
