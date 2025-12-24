using TextOps.Contracts.Messaging;

namespace TextOps.Contracts.Execution;

/// <summary>
/// Represents the result of an orchestrator operation.
/// </summary>
/// <param name="RunId">The run identifier, if applicable.</param>
/// <param name="Outbound">Outbound messages to send to the user.</param>
/// <param name="DispatchedExecution">Whether an execution was dispatched.</param>
/// <param name="Dispatch">The execution dispatch request, if any.</param>
public sealed record OrchestratorResult(
    string? RunId,
    IReadOnlyList<OutboundMessage> Outbound,
    bool DispatchedExecution,
    ExecutionDispatch? Dispatch = null
);

