using TextOps.Contracts.Messaging;

namespace TextOps.Orchestrator.Orchestration;

public sealed record OrchestratorResult(
    string? RunId,
    IReadOnlyList<OutboundMessage> Outbound,
    bool DispatchedExecution,
    ExecutionDispatch? Dispatch = null
);
