using TextOps.Orchestrator.Orchestration;

namespace TextOps.Worker.Stub;

/// <summary>
/// Executes work and reports results back to the orchestrator.
/// </summary>
public interface IWorkerExecutor
{
    /// <summary>
    /// Executes a dispatch request and reports results to the orchestrator.
    /// Returns the orchestrator result from completion callback.
    /// </summary>
    Task<OrchestratorResult> ExecuteAsync(ExecutionDispatch dispatch, CancellationToken cancellationToken);
}

