using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;

namespace TextOps.Worker.Stub;

/// <summary>
/// Stub worker executor that simulates job execution and reports results to the orchestrator.
/// </summary>
public sealed class StubWorkerExecutor : IWorkerExecutor
{
    private readonly IRunOrchestrator _orchestrator;
    private const string WorkerId = "worker-stub";

    public StubWorkerExecutor(IRunOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task<OrchestratorResult> ExecuteAsync(ExecutionDispatch dispatch, CancellationToken cancellationToken)
    {
        // Report execution started
        _orchestrator.OnExecutionStarted(dispatch.RunId, WorkerId);

        // Simulate work execution (1-2 seconds)
        var delay = Random.Shared.Next(1000, 2001);
        await Task.Delay(delay, cancellationToken);

        // Determine success/failure based on job key
        // For now: deterministic success unless jobKey contains "fail"
        var success = !dispatch.JobKey.Contains("fail", StringComparison.OrdinalIgnoreCase);
        var summary = success
            ? $"Job '{dispatch.JobKey}' completed successfully"
            : $"Job '{dispatch.JobKey}' failed (simulated failure)";

        // Report execution completed
        var result = _orchestrator.OnExecutionCompleted(dispatch.RunId, WorkerId, success, summary);

        return result;
    }
}
