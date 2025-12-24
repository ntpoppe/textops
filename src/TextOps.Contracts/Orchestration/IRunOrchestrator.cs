using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;

namespace TextOps.Contracts.Orchestration;

/// <summary>
/// Core orchestrator interface for handling inbound messages and execution lifecycle.
/// </summary>
public interface IRunOrchestrator
{
    /// <summary>
    /// Handles an inbound message with its parsed intent.
    /// </summary>
    OrchestratorResult HandleInbound(InboundMessage msg, ParsedIntent intent);

    /// <summary>
    /// Gets the timeline (run + events) for a given run ID.
    /// </summary>
    RunTimeline GetTimeline(string runId);

    /// <summary>
    /// Called when a worker starts executing a run.
    /// </summary>
    OrchestratorResult OnExecutionStarted(string runId, string workerId);

    /// <summary>
    /// Called when a worker completes executing a run.
    /// </summary>
    OrchestratorResult OnExecutionCompleted(string runId, string workerId, bool success, string summary);
}

