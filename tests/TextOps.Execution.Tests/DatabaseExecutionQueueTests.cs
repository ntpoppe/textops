using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TextOps.Contracts.Execution;
using TextOps.Execution;
using TextOps.Persistence;

namespace TextOps.Execution.Tests;

[TestFixture]
public sealed class DatabaseExecutionQueueTests
{
    private SqliteConnection _connection = null!;
    private TextOpsDbContext _db = null!;
    private DatabaseExecutionQueue _queue = null!;

    [SetUp]
    public void SetUp()
    {
        // Keep connection open for in-memory SQLite to persist
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TextOpsDbContext(options);
        _db.Database.EnsureCreated();
        
        var logger = NullLogger<DatabaseExecutionQueue>.Instance;
        _queue = new DatabaseExecutionQueue(_db, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Enqueue_AddsEntryToDatabase()
    {
        var dispatch = new ExecutionDispatch("run-1", "test-job");
        
        await _queue.EnqueueAsync(dispatch);
        
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
        
        await _queue.EnqueueAsync(dispatch);
        await _queue.EnqueueAsync(dispatch);
        
        var count = await _db.ExecutionQueue.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ClaimNextAsync_ReturnsOldestPendingEntry()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "job-1"));
        await Task.Delay(10); // Ensure different timestamps
        await _queue.EnqueueAsync(new ExecutionDispatch("run-2", "job-2"));

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
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
        
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("processing"));
        Assert.That(entry.LockedBy, Is.EqualTo("worker-1"));
        Assert.That(entry.LockedAt, Is.Not.Null);
    }

    [Test]
    public async Task ClaimNextAsync_SkipsAlreadyProcessingEntries()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "job-1"));
        await _queue.EnqueueAsync(new ExecutionDispatch("run-2", "job-2"));
        
        await _queue.ClaimNextAsync("worker-1");
        var secondClaim = await _queue.ClaimNextAsync("worker-2");
        
        Assert.That(secondClaim, Is.Not.Null);
        Assert.That(secondClaim!.RunId, Is.EqualTo("run-2"));
    }

    [Test]
    public async Task CompleteAsync_MarkesEntryAsCompleted()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
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
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        await _queue.CompleteAsync(claimed!.QueueId, success: false, errorMessage: "Something went wrong");
        
        var entry = await _db.ExecutionQueue.FirstAsync();
        Assert.That(entry.Status, Is.EqualTo("failed"));
        Assert.That(entry.LastError, Is.EqualTo("Something went wrong"));
    }

    [Test]
    public async Task ReleaseAsync_ReturnsEntryToPending()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
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
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
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
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
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
            await _queue.EnqueueAsync(new ExecutionDispatch($"run-{i}", $"job-{i}"));
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
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
        
        var claim1 = await _queue.ClaimNextAsync("worker-1");
        Assert.That(claim1!.Attempts, Is.EqualTo(1));
        
        await _queue.ReleaseAsync(claim1.QueueId, "Retry");
        
        var claim2 = await _queue.ClaimNextAsync("worker-2");
        Assert.That(claim2!.Attempts, Is.EqualTo(2));
    }
}
