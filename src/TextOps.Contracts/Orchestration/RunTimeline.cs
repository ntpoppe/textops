using TextOps.Contracts.Runs;

namespace TextOps.Contracts.Orchestration;

/// <summary>
/// Represents a run and its complete event history.
/// </summary>
public sealed record RunTimeline(
    Run Run,
    IReadOnlyList<RunEvent> Events
);

