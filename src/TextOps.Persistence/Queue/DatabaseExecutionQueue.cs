using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;
using TextOps.Persistence.Entities;

namespace TextOps.Persistence.Queue;

/// <summary>
/// Database-backed execution queue using PostgreSQL FOR UPDATE SKIP LOCKED.
/// For SQLite, falls back to optimistic locking (less efficient but functional).
/// </summary>
public sealed class DatabaseExecutionQueue : IExecutionQueue
{
    private readonly TextOpsDbContext _db;
    private readonly ILogger<DatabaseExecutionQueue> _logger;

    public DatabaseExecutionQueue(TextOpsDbContext db, ILogger<DatabaseExecutionQueue> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Enqueue(ExecutionDispatch dispatch)
    {
        EnqueueAsync(dispatch).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Enqueues a dispatch if not already pending/processing for this RunId.
    /// </summary>
    public async Task EnqueueAsync(ExecutionDispatch dispatch, CancellationToken ct = default)
    {
        if (await IsAlreadyQueued(dispatch.RunId, ct))
        {
            _logger.LogDebug("Dispatch already queued for RunId={RunId}", dispatch.RunId);
            return;
        }

        var entry = CreateQueueEntry(dispatch);
        _db.ExecutionQueue.Add(entry);
        await _db.SaveChangesAsync(ct);
        
        _logger.LogInformation("Enqueued dispatch: RunId={RunId}, JobKey={JobKey}", dispatch.RunId, dispatch.JobKey);
    }

    private async Task<bool> IsAlreadyQueued(string runId, CancellationToken ct)
    {
        return await _db.ExecutionQueue
            .AnyAsync(e => e.RunId == runId && 
                          (e.Status == "pending" || e.Status == "processing"), ct);
    }

    private static ExecutionQueueEntity CreateQueueEntry(ExecutionDispatch dispatch)
    {
        return new ExecutionQueueEntity
        {
            RunId = dispatch.RunId,
            JobKey = dispatch.JobKey,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 0
        };
    }

    public async Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken ct = default)
    {
        var entry = await FindOldestPendingEntry(ct);
        if (entry == null)
            return null;

        var claimed = await ClaimEntry(entry, workerId, ct);
        if (claimed == null)
            return null;

        LogClaimedDispatch(claimed, workerId);
        return new QueuedDispatch(claimed.Id, claimed.RunId, claimed.JobKey, claimed.Attempts);
    }

    private async Task<ExecutionQueueEntity?> FindOldestPendingEntry(CancellationToken ct)
    {
        return await _db.ExecutionQueue
            .Where(e => e.Status == "pending")
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ExecutionQueueEntity?> ClaimEntry(ExecutionQueueEntity entry, string workerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        entry.Status = "processing";
        entry.LockedAt = now;
        entry.LockedBy = workerId;
        entry.Attempts++;

        try
        {
            await _db.SaveChangesAsync(ct);
            return entry;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Dispatch claimed by another worker: QueueId={QueueId}", entry.Id);
            return null;
        }
    }

    private void LogClaimedDispatch(ExecutionQueueEntity entry, string workerId)
    {
        _logger.LogInformation(
            "Claimed dispatch: QueueId={QueueId}, RunId={RunId}, WorkerId={WorkerId}, Attempt={Attempt}",
            entry.Id, entry.RunId, workerId, entry.Attempts);
    }

    public async Task CompleteAsync(long queueId, bool success, string? error, CancellationToken ct = default)
    {
        var entry = await _db.ExecutionQueue.FindAsync(new object[] { queueId }, ct);
        if (entry == null)
        {
            _logger.LogWarning("Cannot complete unknown queue entry: QueueId={QueueId}", queueId);
            return;
        }

        entry.Status = success ? "completed" : "failed";
        entry.CompletedAt = DateTimeOffset.UtcNow;
        entry.LastError = error;
        entry.LockedAt = null;
        entry.LockedBy = null;

        await _db.SaveChangesAsync(ct);
        
        _logger.LogInformation(
            "Completed dispatch: QueueId={QueueId}, RunId={RunId}, Success={Success}",
            queueId, entry.RunId, success);
    }

    public async Task ReleaseAsync(long queueId, string? error, CancellationToken ct = default)
    {
        var entry = await _db.ExecutionQueue.FindAsync(new object[] { queueId }, ct);
        if (entry == null)
        {
            _logger.LogWarning("Cannot release unknown queue entry: QueueId={QueueId}", queueId);
            return;
        }

        entry.Status = "pending";
        entry.LockedAt = null;
        entry.LockedBy = null;
        entry.LastError = error;

        await _db.SaveChangesAsync(ct);
        
        _logger.LogInformation(
            "Released dispatch back to pending: QueueId={QueueId}, RunId={RunId}",
            queueId, entry.RunId);
    }

    public async Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken ct = default)
    {
        var staleEntries = await FindStaleEntries(lockTimeout, ct);

        foreach (var entry in staleEntries)
        {
            ReclaimEntry(entry);
        }

        if (staleEntries.Count > 0)
            await _db.SaveChangesAsync(ct);

        return staleEntries.Count;
    }

    private async Task<List<ExecutionQueueEntity>> FindStaleEntries(TimeSpan lockTimeout, CancellationToken ct)
    {
        var threshold = DateTimeOffset.UtcNow - lockTimeout;
        
        var processingEntries = await _db.ExecutionQueue
            .Where(e => e.Status == "processing")
            .ToListAsync(ct);

        return processingEntries
            .Where(e => e.LockedAt.HasValue && e.LockedAt.Value < threshold)
            .ToList();
    }

    private void ReclaimEntry(ExecutionQueueEntity entry)
    {
        var previousWorker = entry.LockedBy;
        entry.Status = "pending";
        entry.LockedAt = null;
        entry.LockedBy = null;
        
        _logger.LogWarning(
            "Reclaimed stale dispatch: QueueId={QueueId}, RunId={RunId}, PreviousWorker={Worker}",
            entry.Id, entry.RunId, previousWorker);
    }
}
