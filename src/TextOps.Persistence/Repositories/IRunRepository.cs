using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Runs;

namespace TextOps.Persistence.Repositories;

/// <summary>
/// Repository interface for run persistence operations.
/// </summary>
public interface IRunRepository
{
    /// <summary>
    /// Attempts to mark an inbound message as processed (idempotency guard).
    /// Returns true if already processed (duplicate), false if newly processed.
    /// </summary>
    Task<bool> IsInboxProcessedAsync(string channelId, string providerMessageId, CancellationToken ct = default);

    /// <summary>
    /// Marks an inbound message as processed and optionally associates it with a run.
    /// </summary>
    Task MarkInboxProcessedAsync(string channelId, string providerMessageId, string? runId, CancellationToken ct = default);

    /// <summary>
    /// Creates a run with its initial events atomically.
    /// </summary>
    Task CreateRunAsync(Run run, IEnumerable<RunEvent> events, CancellationToken ct = default);

    /// <summary>
    /// Attempts to update a run's status atomically with optimistic concurrency.
    /// Returns the updated run if successful, null if the expected status didn't match.
    /// </summary>
    Task<Run?> TryUpdateRunAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus newStatus,
        IEnumerable<RunEvent> events,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to update a run's status from any of the expected statuses.
    /// Returns the updated run if successful, null if none of the expected statuses matched.
    /// </summary>
    Task<Run?> TryUpdateRunFromMultipleAsync(
        string runId,
        RunStatus[] expectedStatuses,
        RunStatus newStatus,
        IEnumerable<RunEvent> events,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a run by ID.
    /// </summary>
    Task<Run?> GetRunAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Gets a run's complete timeline (run + events).
    /// </summary>
    Task<RunTimeline?> GetTimelineAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a run (for concurrency checks).
    /// </summary>
    Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken ct = default);
}

