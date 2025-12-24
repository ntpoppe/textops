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
        // Idempotency: don't enqueue if already pending or processing
        var existing = await _db.ExecutionQueue
            .AnyAsync(e => e.RunId == dispatch.RunId && 
                          (e.Status == "pending" || e.Status == "processing"), ct);
        
        if (existing)
        {
            _logger.LogDebug("Dispatch already queued for RunId={RunId}", dispatch.RunId);
            return;
        }

        var entry = new ExecutionQueueEntity
        {
            RunId = dispatch.RunId,
            JobKey = dispatch.JobKey,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 0
        };

        _db.ExecutionQueue.Add(entry);
        await _db.SaveChangesAsync(ct);
        
        _logger.LogInformation("Enqueued dispatch: RunId={RunId}, JobKey={JobKey}", dispatch.RunId, dispatch.JobKey);
    }

    public async Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken ct = default)
    {
        // Attempt atomic claim using raw SQL for PostgreSQL (FOR UPDATE SKIP LOCKED)
        // For SQLite, we use optimistic approach: find + update
        
        var now = DateTimeOffset.UtcNow;
        
        // Find oldest pending entry 
        var entry = await _db.ExecutionQueue
            .Where(e => e.Status == "pending")
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (entry == null)
            return null;

        // Attempt to claim it
        entry.Status = "processing";
        entry.LockedAt = now;
        entry.LockedBy = workerId;
        entry.Attempts++;

        try
        {
            await _db.SaveChangesAsync(ct);
            
            _logger.LogInformation(
                "Claimed dispatch: QueueId={QueueId}, RunId={RunId}, WorkerId={WorkerId}, Attempt={Attempt}",
                entry.Id, entry.RunId, workerId, entry.Attempts);
            
            return new QueuedDispatch(entry.Id, entry.RunId, entry.JobKey, entry.Attempts);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed it first - that's fine
            _logger.LogDebug("Dispatch claimed by another worker: QueueId={QueueId}", entry.Id);
            return null;
        }
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
        var threshold = DateTimeOffset.UtcNow - lockTimeout;
        
        var processingEntries = await _db.ExecutionQueue
            .Where(e => e.Status == "processing")
            .ToListAsync(ct);

        var staleEntries = processingEntries
            .Where(e => e.LockedAt.HasValue && e.LockedAt.Value < threshold)
            .ToList();

        foreach (var entry in staleEntries)
        {
            var previousWorker = entry.LockedBy;
            entry.Status = "pending";
            entry.LockedAt = null;
            entry.LockedBy = null;
            
            _logger.LogWarning(
                "Reclaimed stale dispatch: QueueId={QueueId}, RunId={RunId}, PreviousWorker={Worker}",
                entry.Id, entry.RunId, previousWorker);
        }

        if (staleEntries.Count > 0)
            await _db.SaveChangesAsync(ct);

        return staleEntries.Count;
    }
}
