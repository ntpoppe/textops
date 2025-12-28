using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using TextOps.Contracts.Execution;
using TextOps.Persistence;
using TextOps.Persistence.Queue;

namespace TextOps.Persistence.Tests.Queue;

[TestFixture]
public sealed class DatabaseExecutionQueueTests
{
    private TextOpsDbContext _db = null!;
    private DatabaseExecutionQueue _queue = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new TextOpsDbContext(options);
        var logger = NullLogger<DatabaseExecutionQueue>.Instance;
        _queue = new DatabaseExecutionQueue(_db, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Enqueue_AddsEntryToDatabase()
    {
        var dispatch = new ExecutionDispatch("run-1", "test-job");
        
        _queue.Enqueue(dispatch);
        
        var entry = await _db.ExecutionQueue.FirstOrDefaultAsync();
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.RunId, Is.EqualTo("run-1"));
        Assert.That(entry.JobKey, Is.EqualTo("test-job"));
        Assert.That(entry.Status, Is.EqualTo("pending"));
    }

    [Test]
    public async Task Enqueue_SkipsDuplicatePendingDispatch()
    {
        var dispatch = new ExecutionDispatch("run-1", "test-job");
        
        _queue.Enqueue(dispatch);
        _queue.Enqueue(dispatch);
        
        var count = await _db.ExecutionQueue.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ClaimNextAsync_ReturnsOldestPendingEntry()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "job-1"));
        await Task.Delay(10); // Ensure different timestamps
        _queue.Enqueue(new ExecutionDispatch("run-2", "job-2"));

        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        Assert.That(claimed, Is.Not.Null);
        Assert.That(claimed!.RunId, Is.EqualTo("run-1"));
        Assert.That(claimed.Attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task ClaimNextAsync_ReturnsNullWhenQueueEmpty()
    {
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        Assert.That(claimed, Is.Null);
    }

    [Test]
    public async Task ClaimNextAsync_SetsProcessingStatus()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("processing"));
        Assert.That(entry.LockedBy, Is.EqualTo("worker-1"));
        Assert.That(entry.LockedAt, Is.Not.Null);
    }

    [Test]
    public async Task ClaimNextAsync_SkipsAlreadyProcessingEntries()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "job-1"));
        _queue.Enqueue(new ExecutionDispatch("run-2", "job-2"));
        
        await _queue.ClaimNextAsync("worker-1");
        var secondClaim = await _queue.ClaimNextAsync("worker-2");
        
        Assert.That(secondClaim, Is.Not.Null);
        Assert.That(secondClaim!.RunId, Is.EqualTo("run-2"));
    }

    [Test]
    public async Task CompleteAsync_MarkesEntryAsCompleted()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        await _queue.CompleteAsync(claimed!.QueueId, success: true, errorMessage: null);
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("completed"));
        Assert.That(entry.CompletedAt, Is.Not.Null);
        Assert.That(entry.LockedBy, Is.Null);
        Assert.That(entry.LockedAt, Is.Null);
    }

    [Test]
    public async Task CompleteAsync_MarksEntryAsFailedWithError()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        await _queue.CompleteAsync(claimed!.QueueId, success: false, errorMessage: "Something went wrong");
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("failed"));
        Assert.That(entry.LastError, Is.EqualTo("Something went wrong"));
    }

    [Test]
    public async Task ReleaseAsync_ReturnsEntryToPending()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        await _queue.ReleaseAsync(claimed!.QueueId, errorMessage: "Retrying");
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("pending"));
        Assert.That(entry.LockedBy, Is.Null);
        Assert.That(entry.LockedAt, Is.Null);
        Assert.That(entry.LastError, Is.EqualTo("Retrying"));
    }

    [Test]
    public async Task ReclaimStaleAsync_ReclaimsOldLockedEntries()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        // Manually set the lock time to the past
        var entry = await _db.ExecutionQueue.FirstAsync();
        entry.LockedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();
        
        var reclaimed = await _queue.ReclaimStaleAsync(TimeSpan.FromMinutes(5));
        
        Assert.That(reclaimed, Is.EqualTo(1));
        entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("pending"));
    }

    [Test]
    public async Task ReclaimStaleAsync_DoesNotReclaimRecentLocks()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        await _queue.ClaimNextAsync("worker-1");
        
        var reclaimed = await _queue.ReclaimStaleAsync(TimeSpan.FromMinutes(5));
        
        Assert.That(reclaimed, Is.EqualTo(0));
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("processing"));
    }

    [Test]
    public async Task MultipleWorkers_ClaimDifferentEntries()
    {
        for (int i = 1; i <= 5; i++)
        {
            _queue.Enqueue(new ExecutionDispatch($"run-{i}", $"job-{i}"));
        }

        var claims = new List<QueuedDispatch?>();
        for (int i = 1; i <= 5; i++)
        {
            var claim = await _queue.ClaimNextAsync($"worker-{i}");
            claims.Add(claim);
        }

        Assert.That(claims, Has.All.Not.Null);
        var runIds = claims.Select(c => c!.RunId).ToHashSet();
        Assert.That(runIds.Count, Is.EqualTo(5), "Each worker should get a different entry");
    }

    [Test]
    public async Task ClaimNextAsync_IncrementsAttemptCount()
    {
        _queue.Enqueue(new ExecutionDispatch("run-1", "test-job"));
        
        var claim1 = await _queue.ClaimNextAsync("worker-1");
        Assert.That(claim1!.Attempts, Is.EqualTo(1));
        
        await _queue.ReleaseAsync(claim1.QueueId, "Retry");
        
        var claim2 = await _queue.ClaimNextAsync("worker-2");
        Assert.That(claim2!.Attempts, Is.EqualTo(2));
    }
}
