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
        _orchestrator.OnExecutionStarted(dispatch.RunId, WorkerId);

        await SimulateWork(cancellationToken);

        var success = DetermineSuccess(dispatch.JobKey);
        var summary = CreateSummary(dispatch.JobKey, success);

        var result = _orchestrator.OnExecutionCompleted(dispatch.RunId, WorkerId, success, summary);

        return result;
    }

    private static async Task SimulateWork(CancellationToken cancellationToken)
    {
        var delay = Random.Shared.Next(1000, 2001);
        await Task.Delay(delay, cancellationToken);
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
