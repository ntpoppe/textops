namespace TextOps.Contracts.Execution;

/// <summary>
/// Reads execution dispatch requests from a queue.
/// </summary>
public interface IExecutionQueueReader
{
    /// <summary>
    /// Asynchronously reads all execution dispatch requests from the queue.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the read operation.</param>
    /// <returns>An async enumerable of execution dispatch requests.</returns>
    IAsyncEnumerable<ExecutionDispatch> ReadAllAsync(CancellationToken cancellationToken);
}

