using Microsoft.EntityFrameworkCore;
using TextOps.Contracts.Runs;
using TextOps.Persistence;
using TextOps.Persistence.Repositories;

namespace TextOps.Persistence.Tests.Repositories;

[TestFixture]
public class EfRunRepositoryTests
{
    private TextOpsDbContext _db = null!;
    private EfRunRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new TextOpsDbContext(options);
        _repo = new EfRunRepository(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    // ===========================================
    // Inbox Deduplication Tests
    // ===========================================

    [Test]
    public async Task IsInboxProcessed_NewMessage_ReturnsFalse()
    {
        var result = await _repo.IsInboxProcessedAsync("dev", "msg-001");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsInboxProcessed_AfterMarkProcessed_ReturnsTrue()
    {
        await _repo.MarkInboxProcessedAsync("dev", "msg-001", null);

        var result = await _repo.IsInboxProcessedAsync("dev", "msg-001");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsInboxProcessed_DifferentChannel_ReturnsFalse()
    {
        await _repo.MarkInboxProcessedAsync("dev", "msg-001", null);

        var result = await _repo.IsInboxProcessedAsync("other-channel", "msg-001");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsInboxProcessed_DifferentMessageId_ReturnsFalse()
    {
        await _repo.MarkInboxProcessedAsync("dev", "msg-001", null);

        var result = await _repo.IsInboxProcessedAsync("dev", "msg-002");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task MarkInboxProcessed_WithRunId_AssociatesRunId()
    {
        await _repo.MarkInboxProcessedAsync("dev", "msg-001", "RUN123");

        var entry = await _db.InboxEntries.FindAsync("dev", "msg-001");

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.RunId, Is.EqualTo("RUN123"));
    }

    // ===========================================
    // Run Creation Tests
    // ===========================================

    [Test]
    public async Task CreateRun_StoresRunAndEvents()
    {
        var run = CreateTestRun("RUN001");
        var events = new[]
        {
            new RunEvent("RUN001", "RunCreated", DateTimeOffset.UtcNow, "user:test", new { }),
            new RunEvent("RUN001", "ApprovalRequested", DateTimeOffset.UtcNow, "system", new { })
        };

        await _repo.CreateRunAsync(run, events);

        var storedRun = await _repo.GetRunAsync("RUN001");
        Assert.That(storedRun, Is.Not.Null);
        Assert.That(storedRun!.JobKey, Is.EqualTo("test-job"));
        Assert.That(storedRun.Status, Is.EqualTo(RunStatus.AwaitingApproval));

        var timeline = await _repo.GetTimelineAsync("RUN001");
        Assert.That(timeline, Is.Not.Null);
        Assert.That(timeline!.Events.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateRun_WithEmptyEvents_StoresRunOnly()
    {
        var run = CreateTestRun("RUN002");

        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var storedRun = await _repo.GetRunAsync("RUN002");
        Assert.That(storedRun, Is.Not.Null);

        var timeline = await _repo.GetTimelineAsync("RUN002");
        Assert.That(timeline!.Events.Count, Is.EqualTo(0));
    }

    // ===========================================
    // Run Update Tests
    // ===========================================

    [Test]
    public async Task TryUpdateRun_ValidTransition_UpdatesAndReturnsRun()
    {
        var run = CreateTestRun("RUN003");
        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var events = new[] { new RunEvent("RUN003", "RunApproved", DateTimeOffset.UtcNow, "user:test", new { }) };
        var result = await _repo.TryUpdateRunAsync("RUN003", RunStatus.AwaitingApproval, RunStatus.Dispatching, events);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Status, Is.EqualTo(RunStatus.Dispatching));

        var timeline = await _repo.GetTimelineAsync("RUN003");
        Assert.That(timeline!.Events.Count, Is.EqualTo(1));
        Assert.That(timeline.Events[0].Type, Is.EqualTo("RunApproved"));
    }

    [Test]
    public async Task TryUpdateRun_WrongExpectedStatus_ReturnsNull()
    {
        var run = CreateTestRun("RUN004");
        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var result = await _repo.TryUpdateRunAsync("RUN004", RunStatus.Running, RunStatus.Succeeded, Array.Empty<RunEvent>());

        Assert.That(result, Is.Null);

        var storedRun = await _repo.GetRunAsync("RUN004");
        Assert.That(storedRun!.Status, Is.EqualTo(RunStatus.AwaitingApproval)); // Unchanged
    }

    [Test]
    public async Task TryUpdateRun_NonExistentRun_ReturnsNull()
    {
        var result = await _repo.TryUpdateRunAsync("NONEXISTENT", RunStatus.AwaitingApproval, RunStatus.Dispatching, Array.Empty<RunEvent>());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryUpdateRunFromMultiple_MatchesOneOfExpectedStatuses_Updates()
    {
        var run = CreateTestRun("RUN005", RunStatus.Running);
        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var result = await _repo.TryUpdateRunFromMultipleAsync(
            "RUN005",
            new[] { RunStatus.Dispatching, RunStatus.Running },
            RunStatus.Succeeded,
            Array.Empty<RunEvent>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Status, Is.EqualTo(RunStatus.Succeeded));
    }

    [Test]
    public async Task TryUpdateRunFromMultiple_NoMatchingStatus_ReturnsNull()
    {
        var run = CreateTestRun("RUN006", RunStatus.AwaitingApproval);
        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var result = await _repo.TryUpdateRunFromMultipleAsync(
            "RUN006",
            new[] { RunStatus.Running, RunStatus.Dispatching },
            RunStatus.Succeeded,
            Array.Empty<RunEvent>());

        Assert.That(result, Is.Null);
    }

    // ===========================================
    // Timeline Tests
    // ===========================================

    [Test]
    public async Task GetTimeline_NonExistentRun_ReturnsNull()
    {
        var result = await _repo.GetTimelineAsync("NONEXISTENT");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetTimeline_EventsAreOrderedByTimeAndId()
    {
        var run = CreateTestRun("RUN007");
        var baseTime = DateTimeOffset.UtcNow;

        // Create events out of order
        var events = new[]
        {
            new RunEvent("RUN007", "Third", baseTime.AddSeconds(2), "system", new { }),
            new RunEvent("RUN007", "First", baseTime, "user:test", new { }),
            new RunEvent("RUN007", "Second", baseTime.AddSeconds(1), "system", new { })
        };

        await _repo.CreateRunAsync(run, events);

        var timeline = await _repo.GetTimelineAsync("RUN007");

        Assert.That(timeline, Is.Not.Null);
        Assert.That(timeline!.Events.Count, Is.EqualTo(3));
        Assert.That(timeline.Events[0].Type, Is.EqualTo("First"));
        Assert.That(timeline.Events[1].Type, Is.EqualTo("Second"));
        Assert.That(timeline.Events[2].Type, Is.EqualTo("Third"));
    }

    // ===========================================
    // GetRunStatus Tests
    // ===========================================

    [Test]
    public async Task GetRunStatus_ExistingRun_ReturnsStatus()
    {
        var run = CreateTestRun("RUN008", RunStatus.Running);
        await _repo.CreateRunAsync(run, Array.Empty<RunEvent>());

        var result = await _repo.GetRunStatusAsync("RUN008");

        Assert.That(result, Is.EqualTo(RunStatus.Running));
    }

    [Test]
    public async Task GetRunStatus_NonExistentRun_ReturnsNull()
    {
        var result = await _repo.GetRunStatusAsync("NONEXISTENT");

        Assert.That(result, Is.Null);
    }

    // ===========================================
    // Helpers
    // ===========================================

    private static Run CreateTestRun(string runId, RunStatus status = RunStatus.AwaitingApproval)
    {
        return new Run(
            RunId: runId,
            JobKey: "test-job",
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            RequestedByAddress: "user:test",
            ChannelId: "dev",
            ConversationId: "conv-001"
        );
    }
}

