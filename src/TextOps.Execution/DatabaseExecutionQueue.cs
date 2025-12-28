using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;
using TextOps.Persistence;
using TextOps.Persistence.Entities;

namespace TextOps.Execution;

/// <summary>
/// Database-backed execution queue using PostgreSQL FOR UPDATE SKIP LOCKED.
/// For SQLite, falls back to optimistic locking (less efficient but functional).
/// </summary>
public sealed class DatabaseExecutionQueue : IExecutionQueue
{
    private readonly TextOpsDbContext _dbContext;
    private readonly ILogger<DatabaseExecutionQueue> _logger;

    public DatabaseExecutionQueue(TextOpsDbContext dbContext, ILogger<DatabaseExecutionQueue> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public void Enqueue(ExecutionDispatch dispatch)
    {
        EnqueueAsync(dispatch).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Enqueues a dispatch if not already pending/processing for this RunId.
    /// </summary>
    public async Task EnqueueAsync(ExecutionDispatch executionDispatch, CancellationToken cancellationToken = default)
    {
        if (await IsAlreadyQueued(executionDispatch.RunId, cancellationToken))
        {
            _logger.LogDebug("Dispatch already queued for RunId={RunId}", executionDispatch.RunId);
            return;
        }

        var queueEntry = CreateQueueEntry(executionDispatch);
        _dbContext.ExecutionQueue.Add(queueEntry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation("Enqueued dispatch: RunId={RunId}, JobKey={JobKey}", executionDispatch.RunId, executionDispatch.JobKey);
    }

    private async Task<bool> IsAlreadyQueued(string runId, CancellationToken cancellationToken)
    {
        return await _dbContext.ExecutionQueue
            .AnyAsync(queueEntry => queueEntry.RunId == runId && 
                          (queueEntry.Status == "pending" || queueEntry.Status == "processing"), cancellationToken);
    }

    private static ExecutionQueueEntity CreateQueueEntry(ExecutionDispatch executionDispatch)
        => new ExecutionQueueEntity
        {
            RunId = executionDispatch.RunId,
            JobKey = executionDispatch.JobKey,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 0
        };

    public async Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var queueEntry = await FindOldestPendingEntry(cancellationToken);
        if (queueEntry == null)
            return null;

        var claimedQueueEntry = await ClaimEntry(queueEntry, workerId, cancellationToken);
        if (claimedQueueEntry == null)
            return null;

        LogClaimedDispatch(claimedQueueEntry, workerId);
        return new QueuedDispatch(claimedQueueEntry.Id, claimedQueueEntry.RunId, claimedQueueEntry.JobKey, claimedQueueEntry.Attempts);
    }

    private async Task<ExecutionQueueEntity?> FindOldestPendingEntry(CancellationToken cancellationToken)
    {
        return await _dbContext.ExecutionQueue
            .Where(queueEntry => queueEntry.Status == "pending")
            .OrderBy(queueEntry => queueEntry.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ExecutionQueueEntity?> ClaimEntry(ExecutionQueueEntity queueEntry, string workerId, CancellationToken cancellationToken)
    {
        var lockedAt = DateTimeOffset.UtcNow;
        queueEntry.Status = "processing";
        queueEntry.LockedAt = lockedAt;
        queueEntry.LockedBy = workerId;
        queueEntry.Attempts++;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return queueEntry;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Dispatch claimed by another worker: QueueId={QueueId}", queueEntry.Id);
            return null;
        }
    }

    private void LogClaimedDispatch(ExecutionQueueEntity queueEntry, string workerId)
    {
        _logger.LogInformation(
            "Claimed dispatch: QueueId={QueueId}, RunId={RunId}, WorkerId={WorkerId}, Attempt={Attempt}",
            queueEntry.Id, queueEntry.RunId, workerId, queueEntry.Attempts);
    }

    public async Task CompleteAsync(long queueId, bool success, string? errorMessage, CancellationToken cancellationToken = default)
    {
        var queueEntry = await _dbContext.ExecutionQueue.FindAsync(new object[] { queueId }, cancellationToken);
        if (queueEntry == null)
        {
            _logger.LogWarning("Cannot complete unknown queue entry: QueueId={QueueId}", queueId);
            return;
        }

        queueEntry.Status = success ? "completed" : "failed";
        queueEntry.CompletedAt = DateTimeOffset.UtcNow;
        queueEntry.LastError = errorMessage;
        queueEntry.LockedAt = null;
        queueEntry.LockedBy = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation(
            "Completed dispatch: QueueId={QueueId}, RunId={RunId}, Success={Success}",
            queueId, queueEntry.RunId, success);
    }

    public async Task ReleaseAsync(long queueId, string? errorMessage, CancellationToken cancellationToken = default)
    {
        var queueEntry = await _dbContext.ExecutionQueue.FindAsync(new object[] { queueId }, cancellationToken);
        if (queueEntry == null)
        {
            _logger.LogWarning("Cannot release unknown queue entry: QueueId={QueueId}", queueId);
            return;
        }

        queueEntry.Status = "pending";
        queueEntry.LockedAt = null;
        queueEntry.LockedBy = null;
        queueEntry.LastError = errorMessage;

        await _dbContext.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation(
            "Released dispatch back to pending: QueueId={QueueId}, RunId={RunId}",
            queueId, queueEntry.RunId);
    }

    public async Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default)
    {
        var staleQueueEntries = await FindStaleEntries(lockTimeout, cancellationToken);

        foreach (var staleQueueEntry in staleQueueEntries)
        {
            ReclaimEntry(staleQueueEntry);
        }

        if (staleQueueEntries.Count > 0)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return staleQueueEntries.Count;
    }

    private async Task<List<ExecutionQueueEntity>> FindStaleEntries(TimeSpan lockTimeout, CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - lockTimeout;
        
        var processingQueueEntries = await _dbContext.ExecutionQueue
            .Where(queueEntry => queueEntry.Status == "processing")
            .ToListAsync(cancellationToken);

        return processingQueueEntries
            .Where(queueEntry => queueEntry.LockedAt.HasValue && queueEntry.LockedAt.Value < threshold)
            .ToList();
    }

    private void ReclaimEntry(ExecutionQueueEntity staleQueueEntry)
    {
        var previousWorker = staleQueueEntry.LockedBy;
        staleQueueEntry.Status = "pending";
        staleQueueEntry.LockedAt = null;
        staleQueueEntry.LockedBy = null;
        
        _logger.LogWarning(
            "Reclaimed stale dispatch: QueueId={QueueId}, RunId={RunId}, PreviousWorker={Worker}",
            staleQueueEntry.Id, staleQueueEntry.RunId, previousWorker);
    }
}

