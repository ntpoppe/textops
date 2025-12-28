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

    public async Task<OrchestratorResult> ExecuteAsync(ExecutionDispatch executionDispatch, CancellationToken cancellationToken)
    {
        _orchestrator.OnExecutionStarted(executionDispatch.RunId, WorkerId);

        await SimulateWork(cancellationToken);

        var success = DetermineSuccess(executionDispatch.JobKey);
        var executionSummary = CreateSummary(executionDispatch.JobKey, success);

        var orchestratorResult = _orchestrator.OnExecutionCompleted(executionDispatch.RunId, WorkerId, success, executionSummary);

        return orchestratorResult;
    }

    private static async Task SimulateWork(CancellationToken cancellationToken)
    {
        var workDelay = Random.Shared.Next(1000, 2001);
        await Task.Delay(workDelay, cancellationToken);
    }

    private static bool DetermineSuccess(string jobKey)
    {
        return !jobKey.Contains("fail", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateSummary(string jobKey, bool success)
    {
        return success
            ? $"Job '{jobKey}' completed successfully"
            : $"Job '{jobKey}' failed (simulated failure)";
    }
}
