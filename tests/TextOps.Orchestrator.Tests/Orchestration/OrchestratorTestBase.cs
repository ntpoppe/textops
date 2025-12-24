using Microsoft.EntityFrameworkCore;
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
    public virtual void SetUp()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        Db = new TextOpsDbContext(options);
        var repository = new EfRunRepository(Db);
        Orchestrator = new PersistentRunOrchestrator(repository);
        Parser = new DeterministicIntentParser();
    }

    [TearDown]
    public virtual void TearDown()
    {
        Db.Dispose();
    }
}

