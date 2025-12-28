using Microsoft.EntityFrameworkCore;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;
using TextOps.Persistence;
using TextOps.Persistence.Repositories;

namespace TextOps.Orchestrator.Tests.Orchestration;

/// <summary>
/// Base class for orchestrator tests. Sets up PersistentRunOrchestrator with EF Core InMemory.
/// </summary>
public abstract class OrchestratorTestBase
{
    protected TextOpsDbContext Db { get; private set; } = null!;
    protected IRunOrchestrator Orchestrator { get; private set; } = null!;
    protected IIntentParser Parser { get; private set; } = null!;

    [SetUp]
    public virtual Task SetUpAsync()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        Db = new TextOpsDbContext(options);
        var repository = new EntityFrameworkRunRepository(Db);
        var executionQueue = new StubExecutionQueue();
        Orchestrator = new PersistentRunOrchestrator(repository, executionQueue);
        Parser = new DeterministicIntentParser();
        return Task.CompletedTask;
    }

    [TearDown]
    public virtual void TearDown()
    {
        Db.Dispose();
    }
}

internal sealed class StubExecutionQueue : IExecutionQueue
{
    public Task EnqueueAsync(ExecutionDispatch dispatch, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<QueuedDispatch?> ClaimNextAsync(string workerId, CancellationToken cancellationToken = default)
        => Task.FromResult<QueuedDispatch?>(null);

    public Task CompleteAsync(long queueId, bool success, string? errorMessage, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ReleaseAsync(long queueId, string? errorMessage, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> ReclaimStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

