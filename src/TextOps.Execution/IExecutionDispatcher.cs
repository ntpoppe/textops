using TextOps.Contracts.Execution;

namespace TextOps.Execution;

/// <summary>
/// Dispatches execution requests to workers.
/// </summary>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Enqueues an execution dispatch request.
    /// </summary>
    void Enqueue(ExecutionDispatch dispatch);
}
