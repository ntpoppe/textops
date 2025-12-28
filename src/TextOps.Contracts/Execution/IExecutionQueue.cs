namespace TextOps.Contracts.Execution;

/// <summary>
/// Unified execution queue interface. Implementations can be in-memory or database-backed.
/// </summary>
public interface IExecutionQueue : IExecutionDispatcher
{
    /// <summary>
    /// Claims the next pending dispatch for processing. Returns null if none available.
    /// For database queues, this uses FOR UPDATE SKIP LOCKED for safe concurrent access.
    /// </summary>
    Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Marks a claimed dispatch as completed (success or failure).
    /// </summary>
    Task CompleteAsync(long queueId, bool success, string? errorMessage, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Releases a claimed dispatch back to pending (for retry).
    /// </summary>
    Task ReleaseAsync(long queueId, string? errorMessage, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Reclaims entries that have been locked longer than the timeout.
    /// Called periodically to handle worker crashes.
    /// </summary>
    Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default);
}

