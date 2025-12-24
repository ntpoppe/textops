using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TextOps.Contracts.Execution;

namespace TextOps.Execution;

/// <summary>
/// In-memory execution queue using System.Threading.Channels.
/// </summary>
public sealed class InMemoryExecutionQueue : IExecutionDispatcher, IExecutionQueueReader
{
    private readonly Channel<ExecutionDispatch> _channel;

    public InMemoryExecutionQueue()
    {
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<ExecutionDispatch>(options);
    }

    public void Enqueue(ExecutionDispatch dispatch)
    {
        _channel.Writer.TryWrite(dispatch);
    }

    public async IAsyncEnumerable<ExecutionDispatch> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var dispatch in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return dispatch;
        }
    }
}
