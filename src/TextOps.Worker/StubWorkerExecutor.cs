using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;

namespace TextOps.Worker;

/// <summary>
/// Stub worker executor for development and testing.
/// Simulates job execution with a 1-2 second delay.
/// Replace with a real executor implementation for production.
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
        await _orchestrator.OnExecutionStartedAsync(executionDispatch.RunId, WorkerId);

        await SimulateWork(cancellationToken);

        var success = DetermineSuccess(executionDispatch.JobKey);
        var executionSummary = CreateSummary(executionDispatch.JobKey, success);

        var orchestratorResult = await _orchestrator.OnExecutionCompletedAsync(executionDispatch.RunId, WorkerId, success, executionSummary);

        return orchestratorResult;
    }

    private static async Task SimulateWork(CancellationToken cancellationToken)
    {
        var workDelay = Random.Shared.Next(1000, 2001);
        await Task.Delay(workDelay, cancellationToken);
    }

    private static bool DetermineSuccess(string jobKey)
        => !jobKey.Contains("fail", StringComparison.OrdinalIgnoreCase);

    private static string CreateSummary(string jobKey, bool success)
        => success
            ? $"Job '{jobKey}' completed successfully"
            : $"Job '{jobKey}' failed (simulated failure)";
}

