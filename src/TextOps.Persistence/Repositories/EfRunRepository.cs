using Microsoft.EntityFrameworkCore;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Runs;
using TextOps.Persistence.Entities;

namespace TextOps.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRunRepository"/>.
/// </summary>
public sealed class EfRunRepository : IRunRepository
{
    private readonly TextOpsDbContext _db;

    public EfRunRepository(TextOpsDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsInboxProcessedAsync(string channelId, string providerMessageId, CancellationToken ct = default)
    {
        return await _db.InboxEntries
            .AnyAsync(e => e.ChannelId == channelId && e.ProviderMessageId == providerMessageId, ct);
    }

    public async Task MarkInboxProcessedAsync(string channelId, string providerMessageId, string? runId, CancellationToken ct = default)
    {
        var entry = new InboxEntryEntity
        {
            ChannelId = channelId,
            ProviderMessageId = providerMessageId,
            ProcessedAt = DateTimeOffset.UtcNow,
            RunId = runId
        };

        _db.InboxEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    public async Task CreateRunAsync(Run run, IEnumerable<RunEvent> events, CancellationToken ct = default)
    {
        var entity = RunEntity.FromRun(run);
        var eventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();

        _db.Runs.Add(entity);
        _db.RunEvents.AddRange(eventEntities);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<Run?> TryUpdateRunAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus newStatus,
        IEnumerable<RunEvent> events,
        CancellationToken ct = default)
    {
        var entity = await _db.Runs.FindAsync(new object[] { runId }, ct);
        if (entity == null || entity.Status != expectedStatus)
            return null;

        entity.Status = newStatus;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Version++;

        var eventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();
        _db.RunEvents.AddRange(eventEntities);

        try
        {
            await _db.SaveChangesAsync(ct);
            return entity.ToRun();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another process updated the run concurrently
            return null;
        }
    }

    public async Task<Run?> TryUpdateRunFromMultipleAsync(
        string runId,
        RunStatus[] expectedStatuses,
        RunStatus newStatus,
        IEnumerable<RunEvent> events,
        CancellationToken ct = default)
    {
        var entity = await _db.Runs.FindAsync(new object[] { runId }, ct);
        if (entity == null || !expectedStatuses.Contains(entity.Status))
            return null;

        entity.Status = newStatus;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Version++;

        var eventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();
        _db.RunEvents.AddRange(eventEntities);

        try
        {
            await _db.SaveChangesAsync(ct);
            return entity.ToRun();
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public async Task<Run?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        var entity = await _db.Runs.FindAsync(new object[] { runId }, ct);
        return entity?.ToRun();
    }

    public async Task<RunTimeline?> GetTimelineAsync(string runId, CancellationToken ct = default)
    {
        var entity = await _db.Runs
            .Include(r => r.Events)
            .FirstOrDefaultAsync(r => r.RunId == runId, ct);

        if (entity == null)
            return null;

        var run = entity.ToRun();
        // Order events in memory (SQLite doesn't support DateTimeOffset in ORDER BY)
        var events = entity.Events
            .OrderBy(e => e.At)
            .ThenBy(e => e.Id)
            .Select(e => e.ToRunEvent())
            .ToList();

        return new RunTimeline(run, events);
    }

    public async Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken ct = default)
    {
        var entity = await _db.Runs
            .AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct);

        return entity?.Status;
    }
}

