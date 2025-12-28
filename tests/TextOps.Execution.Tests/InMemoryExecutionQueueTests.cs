using NUnit.Framework;
using TextOps.Contracts.Execution;
using TextOps.Execution;

namespace TextOps.Execution.Tests;

[TestFixture]
public sealed class InMemoryExecutionQueueTests
{
    private InMemoryExecutionQueue _queue = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = new InMemoryExecutionQueue();
    }

    [Test]
    public async Task Enqueue_AndClaim_ReturnsDispatch()
    {
        var dispatch = new ExecutionDispatch("run-1", "test-job");
        
        await _queue.EnqueueAsync(dispatch);
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        Assert.That(claimed, Is.Not.Null);
        Assert.That(claimed!.RunId, Is.EqualTo("run-1"));
        Assert.That(claimed.JobKey, Is.EqualTo("test-job"));
    }

    [Test]
    public async Task ClaimNextAsync_ReturnsDispatchesInOrder()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "job-1"));
        await _queue.EnqueueAsync(new ExecutionDispatch("run-2", "job-2"));
        await _queue.EnqueueAsync(new ExecutionDispatch("run-3", "job-3"));

        var claim1 = await _queue.ClaimNextAsync("worker-1");
        var claim2 = await _queue.ClaimNextAsync("worker-1");
        var claim3 = await _queue.ClaimNextAsync("worker-1");
        
        Assert.That(claim1!.RunId, Is.EqualTo("run-1"));
        Assert.That(claim2!.RunId, Is.EqualTo("run-2"));
        Assert.That(claim3!.RunId, Is.EqualTo("run-3"));
    }

    [Test]
    public async Task CompleteAsync_RemovesFromProcessing()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
        var claimed = await _queue.ClaimNextAsync("worker-1");
        
        await _queue.CompleteAsync(claimed!.QueueId, success: true, errorMessage: null);
        
        // Should not error - entry is removed
        await _queue.CompleteAsync(claimed.QueueId, success: true, errorMessage: null);
    }

    [Test]
    public async Task ReleaseAsync_RequeuesWithIncrementedAttempts()
    {
        await _queue.EnqueueAsync(new ExecutionDispatch("run-1", "test-job"));
        
        var claim1 = await _queue.ClaimNextAsync("worker-1");
        Assert.That(claim1!.Attempts, Is.EqualTo(1));
        
        await _queue.ReleaseAsync(claim1.QueueId, errorMessage: "Retry");
        
        var claim2 = await _queue.ClaimNextAsync("worker-1");
        Assert.That(claim2!.RunId, Is.EqualTo("run-1"));
        Assert.That(claim2.Attempts, Is.EqualTo(2));
    }

    [Test]
    public async Task ReclaimStaleAsync_ReturnsZero()
    {
        // In-memory queue doesn't have stale locks
        var reclaimed = await _queue.ReclaimStaleAsync(TimeSpan.FromMinutes(5));
        Assert.That(reclaimed, Is.EqualTo(0));
    }

    [Test]
    public async Task ClaimNextAsync_WithCancellation_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var claimed = await _queue.ClaimNextAsync("worker-1", cts.Token);
        
        Assert.That(claimed, Is.Null);
    }
}

