using System.Threading.Channels;
using TextOps.Orchestrator.Orchestration;

namespace TextOps.Channels.DevApi.Execution;

/// <summary>
/// In-memory execution queue using System.Threading.Channels.
/// </summary>
public sealed class InMemoryExecutionQueue : IExecutionDispatcher
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

    public ChannelReader<ExecutionDispatch> Reader => _channel.Reader;
}

