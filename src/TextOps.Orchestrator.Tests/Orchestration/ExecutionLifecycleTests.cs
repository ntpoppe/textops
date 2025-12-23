using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public sealed class ExecutionLifecycleTests
{
    private InMemoryRunOrchestrator _orchestrator = null!;
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _orchestrator = new InMemoryRunOrchestrator();
        _parser = new DeterministicIntentParser();
    }

    [Test]
    public void OnExecutionStarted_ThenCompleted_Success_TransitionsToSucceeded()
    {
        // Arrange: create and approve run
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Act: execute lifecycle
        _orchestrator.OnExecutionStarted(runId, "worker-1");
        var completionResult = _orchestrator.OnExecutionCompleted(runId, success: true, summary: "ok");

        // Assert: terminal state and events
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Run should be in Succeeded state");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Completion should emit outbound message");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain("succeeded"), "Outbound message should indicate success");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain(runId), "Outbound message should contain run ID");
        });

        var eventTypes = TestHelpers.GetEventTypes(_orchestrator, runId);
        Assert.Multiple(() =>
        {
            Assert.That(eventTypes, Contains.Item("RunCreated"));
            Assert.That(eventTypes, Contains.Item("ApprovalRequested"));
            Assert.That(eventTypes, Contains.Item("RunApproved"));
            Assert.That(eventTypes, Contains.Item("ExecutionDispatched"));
            Assert.That(eventTypes, Contains.Item("ExecutionStarted"));
            Assert.That(eventTypes, Contains.Item("ExecutionSucceeded"));
        });
    }

    [Test]
    public void OnExecutionStarted_ThenCompleted_Failure_TransitionsToFailed()
    {
        // Arrange: create and approve run
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Act: execute lifecycle with failure
        _orchestrator.OnExecutionStarted(runId, "worker-1");
        var completionResult = _orchestrator.OnExecutionCompleted(runId, success: false, summary: "boom");

        // Assert: terminal state and events
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Failed), "Run should be in Failed state");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Completion should emit outbound message");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain("failed"), "Outbound message should indicate failure");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain(runId), "Outbound message should contain run ID");
        });

        var eventTypes = TestHelpers.GetEventTypes(_orchestrator, runId);
        Assert.That(eventTypes, Contains.Item("ExecutionFailed"), "Should have ExecutionFailed event");
    }

    [Test]
    public void OnExecutionCompleted_WithoutStarted_FromDispatching_TransitionsToTerminal()
    {
        // Arrange: create and approve run (now in Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Verify we're in Dispatching
        var timelineBefore = _orchestrator.GetTimeline(runId);
        Assert.That(timelineBefore.Run.Status, Is.EqualTo(RunStatus.Dispatching));

        // Act: complete without started (robustness: handle missed started event)
        var completionResult = _orchestrator.OnExecutionCompleted(runId, success: true, summary: "ok");

        // Assert: transitions to terminal state
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Should transition to Succeeded");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Should emit completion message");
        });

        var eventTypes = TestHelpers.GetEventTypes(_orchestrator, runId);
        Assert.Multiple(() =>
        {
            Assert.That(eventTypes, Contains.Item("ExecutionDispatched"));
            Assert.That(eventTypes, Contains.Item("ExecutionSucceeded"));
            // ExecutionStarted may be absent (that's OK for robustness)
        });
    }

    [Test]
    public void OnExecutionStarted_UnknownRunId_ReturnsErrorOutbound()
    {
        // Act
        var result = _orchestrator.OnExecutionStarted("NOPE", "worker-1");

        // Assert: returns error message, no exception
        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo("NOPE"));
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("unknown run").IgnoreCase, "Error message should mention unknown run");
        });
    }

    [Test]
    public void OnExecutionCompleted_UnknownRunId_ReturnsErrorOutbound()
    {
        // Act
        var result = _orchestrator.OnExecutionCompleted("NOPE", success: true, summary: "ok");

        // Assert: returns error message, no exception
        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo("NOPE"));
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("unknown run").IgnoreCase, "Error message should mention unknown run");
        });
    }

    [Test]
    public void OnExecutionCompleted_InvalidState_ReturnsErrorOutbound()
    {
        // Arrange: create run (AwaitingApproval, not Running/Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        // Act: try to complete from AwaitingApproval
        var result = _orchestrator.OnExecutionCompleted(runId, success: true, summary: "ok");

        // Assert: error message, state unchanged
        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("Cannot complete"));
        });

        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.AwaitingApproval), "State should remain unchanged");
    }
}

