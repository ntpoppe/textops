namespace TextOps.Worker;

/// <summary>
/// Configuration options for the worker service.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>
    /// Unique identifier for this worker instance. Auto-generated if not set.
    /// </summary>
    public string? WorkerId { get; set; }

    /// <summary>
    /// How often to poll the queue when no work is available.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait after an error before retrying.
    /// </summary>
    public TimeSpan ErrorRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of attempts before marking a dispatch as failed.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// How long a lock can be held before being considered stale.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to check for stale locks.
    /// </summary>
    public TimeSpan StaleLockCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
}

