namespace TextOps.Contracts.Execution;

/// <summary>
/// Dispatches execution requests to workers.
/// </summary>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Enqueues an execution dispatch request.
    /// </summary>
    Task EnqueueAsync(ExecutionDispatch dispatch, CancellationToken cancellationToken = default);
}

