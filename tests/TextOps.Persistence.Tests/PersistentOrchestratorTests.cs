using Microsoft.EntityFrameworkCore;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Persistence;
using TextOps.Persistence.Repositories;

namespace TextOps.Persistence.Tests;

[TestFixture]
public class PersistentOrchestratorTests
{
    private TextOpsDbContext _db = null!;
    private EntityFrameworkRunRepository _repo = null!;
    private PersistentRunOrchestrator _orchestrator = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new TextOpsDbContext(options);
        _repo = new EntityFrameworkRunRepository(_db);
        var executionQueue = new StubExecutionQueue();
        _orchestrator = new PersistentRunOrchestrator(_repo, executionQueue);
    }

    [TearDown]
    public async Task TearDown()
    {
        _db.Dispose();
    }

    // ===========================================
    // Inbound Idempotency Tests
    // ===========================================

    [Test]
    public async Task HandleInbound_DuplicateMessage_ReturnsEmptyResult()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        // First call creates run
        var result1 = await _orchestrator.HandleInboundAsync(msg, intent);
        Assert.That(result1.RunId, Is.Not.Null);
        Assert.That(result1.Outbound.Count, Is.EqualTo(1));

        // Second call with same message ID is a no-op
        var result2 = await _orchestrator.HandleInboundAsync(msg, intent);
        Assert.That(result2.RunId, Is.Null);
        Assert.That(result2.Outbound, Is.Empty);
    }

    [Test]
    public async Task HandleInbound_DifferentMessageId_CreatesNewRun()
    {
        var msg1 = CreateInboundMessage("msg-001");
        var msg2 = CreateInboundMessage("msg-002");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        var result1 = await _orchestrator.HandleInboundAsync(msg1, intent);
        var result2 = await _orchestrator.HandleInboundAsync(msg2, intent);

        Assert.That(result1.RunId, Is.Not.Null);
        Assert.That(result2.RunId, Is.Not.Null);
        Assert.That(result1.RunId, Is.Not.EqualTo(result2.RunId));
    }

    // ===========================================
    // Run Creation Tests
    // ===========================================

    [Test]
    public async Task HandleInbound_RunJob_CreatesRunInAwaitingApproval()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        var result = await _orchestrator.HandleInboundAsync(msg, intent);

        Assert.That(result.RunId, Is.Not.Null);
        Assert.That(result.DispatchedExecution, Is.False);

        var timeline = await _orchestrator.GetTimelineAsync(result.RunId!);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.AwaitingApproval));
        Assert.That(timeline.Events.Count, Is.EqualTo(2)); // RunCreated, ApprovalRequested
    }

    // ===========================================
    // Approval Flow Tests
    // ===========================================

    [Test]
    public async Task HandleInbound_ApproveRun_TransitionsToDispatching()
    {
        // Create run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var createResult = await _orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = createResult.RunId!;

        // Approve run
        var approveMsg = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        var approveResult = await _orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        Assert.That(approveResult.RunId, Is.EqualTo(runId));
        Assert.That(approveResult.DispatchedExecution, Is.True);
        Assert.That(approveResult.Dispatch, Is.Null);

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Dispatching));
    }

    [Test]
    public async Task HandleInbound_ApproveAlreadyApprovedRun_ReturnsError()
    {
        // Create and approve run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var result = await _orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = result.RunId!;

        var approveMsg1 = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        await _orchestrator.HandleInboundAsync(approveMsg1, approveIntent);

        // Try to approve again
        var approveMsg2 = CreateInboundMessage("msg-003");
        var approveResult = await _orchestrator.HandleInboundAsync(approveMsg2, approveIntent);

        Assert.That(approveResult.DispatchedExecution, Is.False);
        Assert.That(approveResult.Outbound[0].Body, Does.Contain("Cannot approve"));
    }

    // ===========================================
    // Deny Flow Tests
    // ===========================================

    [Test]
    public async Task HandleInbound_DenyRun_TransitionsToDenied()
    {
        // Create run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var result = await _orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = result.RunId!;

        // Deny run
        var denyMsg = CreateInboundMessage("msg-002");
        var denyIntent = new ParsedIntent(IntentType.DenyRun, $"no {runId}", null, runId);
        var denyResult = await _orchestrator.HandleInboundAsync(denyMsg, denyIntent);

        Assert.That(denyResult.RunId, Is.EqualTo(runId));
        Assert.That(denyResult.DispatchedExecution, Is.False);

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }

    // ===========================================
    // Execution Lifecycle Tests
    // ===========================================

    [Test]
    public async Task OnExecutionStarted_TransitionsToRunning()
    {
        // Create and approve run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var result = await _orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = result.RunId!;

        var approveMsg = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        await _orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        // Start execution
        var startResult = await _orchestrator.OnExecutionStartedAsync(runId, "worker-1");

        Assert.That(result.RunId, Is.EqualTo(runId));

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running));
    }

    [Test]
    public async Task OnExecutionCompleted_Success_TransitionsToSucceeded()
    {
        // Create, approve, and start run
        var runId = await CreateAndStartRunAsync();

        // Complete execution
        var result = await _orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: true, "Completed successfully");

        Assert.That(result.RunId, Is.EqualTo(runId));
        Assert.That(result.Outbound.Count, Is.EqualTo(1));
        Assert.That(result.Outbound[0].Body, Does.Contain("succeeded"));

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded));
    }

    [Test]
    public async Task OnExecutionCompleted_Failure_TransitionsToFailed()
    {
        var runId = await CreateAndStartRunAsync();

        var result = await _orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: false, "Something went wrong");

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Failed));
        Assert.That(result.Outbound[0].Body, Does.Contain("failed"));
    }

    // ===========================================
    // Execution Idempotency Tests
    // ===========================================

    [Test]
    public async Task OnExecutionStarted_AlreadyRunning_IsNoOp()
    {
        var runId = await CreateAndStartRunAsync();

        // Try to start again
        var result = await _orchestrator.OnExecutionStartedAsync(runId, "worker-2");

        Assert.That(result.Outbound, Is.Empty);

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running));
    }

    [Test]
    public async Task OnExecutionCompleted_AlreadyCompleted_IsNoOp()
    {
        var runId = await CreateAndStartRunAsync();

        // Complete once
        await _orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: true, "First completion");

        // Try to complete again
        var result = await _orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: false, "Second completion");

        Assert.That(result.Outbound, Is.Empty);

        var timeline = await _orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded)); // Still succeeded
    }

    // ===========================================
    // Persistence Tests
    // ===========================================

    [Test]
    public async Task GetTimeline_AfterRunCreation_IncludesAllEvents()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var createResult = await _orchestrator.HandleInboundAsync(msg, intent);
        var runId = createResult.RunId!;

        var timeline = await _orchestrator.GetTimelineAsync(runId);

        Assert.That(timeline.Run.RunId, Is.EqualTo(runId));
        Assert.That(timeline.Run.JobKey, Is.EqualTo("backup"));
        Assert.That(timeline.Events.Any(e => e.Type == "RunCreated"), Is.True);
        Assert.That(timeline.Events.Any(e => e.Type == "ApprovalRequested"), Is.True);
    }

    [Test]
    public async Task GetTimeline_NonExistentRun_ThrowsKeyNotFoundException()
    {
        Assert.ThrowsAsync<KeyNotFoundException>(async () => await _orchestrator.GetTimelineAsync("NONEXISTENT"));
    }

    // ===========================================
    // Helpers
    // ===========================================

    private static InboundMessage CreateInboundMessage(string messageId)
    {
        return new InboundMessage(
            ChannelId: "dev",
            ProviderMessageId: messageId,
            Conversation: new ConversationId("conv-001"),
            From: new Address("user:test"),
            To: null,
            Body: "test",
            ReceivedAt: DateTimeOffset.UtcNow,
            ProviderMeta: new Dictionary<string, string>()
        );
    }

    private async Task<string> CreateAndStartRunAsync()
    {
        var createMsg = CreateInboundMessage($"msg-{Guid.NewGuid():N}");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var createResult = await _orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = createResult.RunId!;

        var approveMsg = CreateInboundMessage($"msg-{Guid.NewGuid():N}");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        await _orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        await _orchestrator.OnExecutionStartedAsync(runId, "worker-1");

        return runId;
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
