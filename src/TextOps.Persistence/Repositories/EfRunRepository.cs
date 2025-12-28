using Microsoft.EntityFrameworkCore;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Persistence;
using TextOps.Contracts.Runs;
using TextOps.Persistence.Entities;

namespace TextOps.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRunRepository"/>.
/// </summary>
public sealed class EntityFrameworkRunRepository : IRunRepository
{
    private readonly TextOpsDbContext _dbContext;

    public EntityFrameworkRunRepository(TextOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsInboxProcessedAsync(string channelId, string providerMessageId, CancellationToken cancellationToken = default)
        => await _dbContext.InboxEntries
            .AnyAsync(inboxEntry => inboxEntry.ChannelId == channelId && inboxEntry.ProviderMessageId == providerMessageId, cancellationToken);

    public async Task MarkInboxProcessedAsync(string channelId, string providerMessageId, string? runId, CancellationToken cancellationToken = default)
    {
        var inboxEntry = new InboxEntryEntity
        {
            ChannelId = channelId,
            ProviderMessageId = providerMessageId,
            ProcessedAt = DateTimeOffset.UtcNow,
            RunId = runId
        };

        _dbContext.InboxEntries.Add(inboxEntry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateRunAsync(Run run, IEnumerable<RunEvent> events, CancellationToken cancellationToken = default)
    {
        var runEntity = RunEntity.FromRun(run);
        var runEventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();

        _dbContext.Runs.Add(runEntity);
        _dbContext.RunEvents.AddRange(runEventEntities);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Run?> TryUpdateRunAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus targetStatus,
        IEnumerable<RunEvent> events,
        CancellationToken cancellationToken = default)
    {
        var runEntity = await _dbContext.Runs.FindAsync(new object[] { runId }, cancellationToken);
        if (runEntity == null || runEntity.Status != expectedStatus)
            return null;

        runEntity.Status = targetStatus;
        runEntity.UpdatedAt = DateTimeOffset.UtcNow;
        runEntity.Version++;

        var runEventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();
        _dbContext.RunEvents.AddRange(runEventEntities);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return runEntity.ToRun();
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public async Task<Run?> TryUpdateRunFromMultipleAsync(
        string runId,
        RunStatus[] expectedStatuses,
        RunStatus targetStatus,
        IEnumerable<RunEvent> events,
        CancellationToken cancellationToken = default)
    {
        var runEntity = await _dbContext.Runs.FindAsync(new object[] { runId }, cancellationToken);
        if (runEntity == null || !expectedStatuses.Contains(runEntity.Status))
            return null;

        runEntity.Status = targetStatus;
        runEntity.UpdatedAt = DateTimeOffset.UtcNow;
        runEntity.Version++;

        var runEventEntities = events.Select(RunEventEntity.FromRunEvent).ToList();
        _dbContext.RunEvents.AddRange(runEventEntities);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return runEntity.ToRun();
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public async Task<Run?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var runEntity = await _dbContext.Runs.FindAsync(new object[] { runId }, cancellationToken);
        return runEntity?.ToRun();
    }

    public async Task<RunTimeline?> GetTimelineAsync(string runId, CancellationToken cancellationToken = default)
    {
        var runEntity = await _dbContext.Runs
            .Include(run => run.Events)
            .FirstOrDefaultAsync(run => run.RunId == runId, cancellationToken);

        if (runEntity == null)
            return null;

        var run = runEntity.ToRun();
        // SQLite doesn't support DateTimeOffset in ORDER BY, so order in memory
        var runEvents = runEntity.Events
            .OrderBy(runEvent => runEvent.At)
            .ThenBy(runEvent => runEvent.Id)
            .Select(runEvent => runEvent.ToRunEvent())
            .ToList();

        return new RunTimeline(run, runEvents);
    }

    public async Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        var statusProjection = await _dbContext.Runs
            .AsNoTracking()
            .Where(run => run.RunId == runId)
            .Select(run => new { run.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return statusProjection?.Status;
    }
}

