namespace TextOps.Contracts.Execution;

/// <summary>
/// A dispatch entry with queue metadata for tracking.
/// </summary>
public sealed record QueuedDispatch(
    long QueueId,
    string RunId,
    string JobKey,
    int Attempts
);

