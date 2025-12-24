using Microsoft.EntityFrameworkCore;
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
    private EfRunRepository _repo = null!;
    private PersistentRunOrchestrator _orchestrator = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TextOpsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new TextOpsDbContext(options);
        _repo = new EfRunRepository(_db);
        _orchestrator = new PersistentRunOrchestrator(_repo);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    // ===========================================
    // Inbound Idempotency Tests
    // ===========================================

    [Test]
    public void HandleInbound_DuplicateMessage_ReturnsEmptyResult()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        // First call creates run
        var result1 = _orchestrator.HandleInbound(msg, intent);
        Assert.That(result1.RunId, Is.Not.Null);
        Assert.That(result1.Outbound.Count, Is.EqualTo(1));

        // Second call with same message ID is a no-op
        var result2 = _orchestrator.HandleInbound(msg, intent);
        Assert.That(result2.RunId, Is.Null);
        Assert.That(result2.Outbound, Is.Empty);
    }

    [Test]
    public void HandleInbound_DifferentMessageId_CreatesNewRun()
    {
        var msg1 = CreateInboundMessage("msg-001");
        var msg2 = CreateInboundMessage("msg-002");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        var result1 = _orchestrator.HandleInbound(msg1, intent);
        var result2 = _orchestrator.HandleInbound(msg2, intent);

        Assert.That(result1.RunId, Is.Not.Null);
        Assert.That(result2.RunId, Is.Not.Null);
        Assert.That(result1.RunId, Is.Not.EqualTo(result2.RunId));
    }

    // ===========================================
    // Run Creation Tests
    // ===========================================

    [Test]
    public void HandleInbound_RunJob_CreatesRunInAwaitingApproval()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);

        var result = _orchestrator.HandleInbound(msg, intent);

        Assert.That(result.RunId, Is.Not.Null);
        Assert.That(result.DispatchedExecution, Is.False);

        var timeline = _orchestrator.GetTimeline(result.RunId!);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.AwaitingApproval));
        Assert.That(timeline.Events.Count, Is.EqualTo(2)); // RunCreated, ApprovalRequested
    }

    // ===========================================
    // Approval Flow Tests
    // ===========================================

    [Test]
    public void HandleInbound_ApproveRun_TransitionsToDispatching()
    {
        // Create run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = createResult.RunId!;

        // Approve run
        var approveMsg = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        var approveResult = _orchestrator.HandleInbound(approveMsg, approveIntent);

        Assert.That(approveResult.RunId, Is.EqualTo(runId));
        Assert.That(approveResult.DispatchedExecution, Is.True);
        Assert.That(approveResult.Dispatch, Is.Not.Null);
        Assert.That(approveResult.Dispatch!.RunId, Is.EqualTo(runId));

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Dispatching));
    }

    [Test]
    public void HandleInbound_ApproveAlreadyApprovedRun_ReturnsError()
    {
        // Create and approve run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var runId = _orchestrator.HandleInbound(createMsg, createIntent).RunId!;

        var approveMsg1 = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        _orchestrator.HandleInbound(approveMsg1, approveIntent);

        // Try to approve again
        var approveMsg2 = CreateInboundMessage("msg-003");
        var approveResult = _orchestrator.HandleInbound(approveMsg2, approveIntent);

        Assert.That(approveResult.DispatchedExecution, Is.False);
        Assert.That(approveResult.Outbound[0].Body, Does.Contain("Cannot approve"));
    }

    // ===========================================
    // Deny Flow Tests
    // ===========================================

    [Test]
    public void HandleInbound_DenyRun_TransitionsToDenied()
    {
        // Create run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var runId = _orchestrator.HandleInbound(createMsg, createIntent).RunId!;

        // Deny run
        var denyMsg = CreateInboundMessage("msg-002");
        var denyIntent = new ParsedIntent(IntentType.DenyRun, $"no {runId}", null, runId);
        var denyResult = _orchestrator.HandleInbound(denyMsg, denyIntent);

        Assert.That(denyResult.RunId, Is.EqualTo(runId));
        Assert.That(denyResult.DispatchedExecution, Is.False);

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }

    // ===========================================
    // Execution Lifecycle Tests
    // ===========================================

    [Test]
    public void OnExecutionStarted_TransitionsToRunning()
    {
        // Create and approve run
        var createMsg = CreateInboundMessage("msg-001");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var runId = _orchestrator.HandleInbound(createMsg, createIntent).RunId!;

        var approveMsg = CreateInboundMessage("msg-002");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Start execution
        var result = _orchestrator.OnExecutionStarted(runId, "worker-1");

        Assert.That(result.RunId, Is.EqualTo(runId));

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running));
    }

    [Test]
    public void OnExecutionCompleted_Success_TransitionsToSucceeded()
    {
        // Create, approve, and start run
        var runId = CreateAndStartRun();

        // Complete execution
        var result = _orchestrator.OnExecutionCompleted(runId, "worker-1", success: true, "Completed successfully");

        Assert.That(result.RunId, Is.EqualTo(runId));
        Assert.That(result.Outbound.Count, Is.EqualTo(1));
        Assert.That(result.Outbound[0].Body, Does.Contain("succeeded"));

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded));
    }

    [Test]
    public void OnExecutionCompleted_Failure_TransitionsToFailed()
    {
        var runId = CreateAndStartRun();

        var result = _orchestrator.OnExecutionCompleted(runId, "worker-1", success: false, "Something went wrong");

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Failed));
        Assert.That(result.Outbound[0].Body, Does.Contain("failed"));
    }

    // ===========================================
    // Execution Idempotency Tests
    // ===========================================

    [Test]
    public void OnExecutionStarted_AlreadyRunning_IsNoOp()
    {
        var runId = CreateAndStartRun();

        // Try to start again
        var result = _orchestrator.OnExecutionStarted(runId, "worker-2");

        Assert.That(result.Outbound, Is.Empty);

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running));
    }

    [Test]
    public void OnExecutionCompleted_AlreadyCompleted_IsNoOp()
    {
        var runId = CreateAndStartRun();

        // Complete once
        _orchestrator.OnExecutionCompleted(runId, "worker-1", success: true, "First completion");

        // Try to complete again
        var result = _orchestrator.OnExecutionCompleted(runId, "worker-1", success: false, "Second completion");

        Assert.That(result.Outbound, Is.Empty);

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded)); // Still succeeded
    }

    // ===========================================
    // Persistence Tests
    // ===========================================

    [Test]
    public void GetTimeline_AfterRunCreation_IncludesAllEvents()
    {
        var msg = CreateInboundMessage("msg-001");
        var intent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var runId = _orchestrator.HandleInbound(msg, intent).RunId!;

        var timeline = _orchestrator.GetTimeline(runId);

        Assert.That(timeline.Run.RunId, Is.EqualTo(runId));
        Assert.That(timeline.Run.JobKey, Is.EqualTo("backup"));
        Assert.That(timeline.Events.Any(e => e.Type == "RunCreated"), Is.True);
        Assert.That(timeline.Events.Any(e => e.Type == "ApprovalRequested"), Is.True);
    }

    [Test]
    public void GetTimeline_NonExistentRun_ThrowsKeyNotFoundException()
    {
        Assert.Throws<KeyNotFoundException>(() => _orchestrator.GetTimeline("NONEXISTENT"));
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

    private string CreateAndStartRun()
    {
        var createMsg = CreateInboundMessage($"msg-{Guid.NewGuid():N}");
        var createIntent = new ParsedIntent(IntentType.RunJob, "run backup", "backup", null);
        var runId = _orchestrator.HandleInbound(createMsg, createIntent).RunId!;

        var approveMsg = CreateInboundMessage($"msg-{Guid.NewGuid():N}");
        var approveIntent = new ParsedIntent(IntentType.ApproveRun, $"yes {runId}", null, runId);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        _orchestrator.OnExecutionStarted(runId, "worker-1");

        return runId;
    }
}
