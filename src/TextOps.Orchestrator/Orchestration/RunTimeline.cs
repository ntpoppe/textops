using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Orchestration;

public sealed record RunTimeline(
    Run Run,
    IReadOnlyList<RunEvent> Events
);