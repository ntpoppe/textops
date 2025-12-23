using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;

namespace TextOps.Orchestrator.Orchestration;

public interface IRunOrchestrator
{
    OrchestratorResult HandleInbound(InboundMessage msg, ParsedIntent intent);
    RunTimeline GetTimeline(string runId);
    OrchestratorResult OnExecutionStarted(string runId, string workerId);
    OrchestratorResult OnExecutionCompleted(string runId, bool success, string summary);
}
