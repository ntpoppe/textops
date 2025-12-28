using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public sealed class ExecutionLifecycleTests : OrchestratorTestBase
{
    [Test]
    public async Task OnExecutionStarted_ThenCompleted_Success_TransitionsToSucceeded()
    {
        // Arrange: create and approve run
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(Parser, createMsg);
        var createResult = await Orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(Parser, approveMsg);
        await Orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        // Act: execute lifecycle
        await Orchestrator.OnExecutionStartedAsync(runId, "worker-1");
        var completionResult = await Orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: true, summary: "ok");

        // Assert: terminal state and events
        var timeline = await Orchestrator.GetTimelineAsync(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Run should be in Succeeded state");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Completion should emit outbound message");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain("succeeded"), "Outbound message should indicate success");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain(runId), "Outbound message should contain run ID");
        });

        var eventTypes = await TestHelpers.GetEventTypesAsync(Orchestrator, runId);
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
    public async Task OnExecutionStarted_ThenCompleted_Failure_TransitionsToFailed()
    {
        // Arrange: create and approve run
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(Parser, createMsg);
        var createResult = await Orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(Parser, approveMsg);
        await Orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        // Act: execute lifecycle with failure
        await Orchestrator.OnExecutionStartedAsync(runId, "worker-1");
        var completionResult = await Orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: false, summary: "boom");

        // Assert: terminal state and events
        var timeline = await Orchestrator.GetTimelineAsync(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Failed), "Run should be in Failed state");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Completion should emit outbound message");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain("failed"), "Outbound message should indicate failure");
            Assert.That(completionResult.Outbound[0].Body, Does.Contain(runId), "Outbound message should contain run ID");
        });

        var eventTypes = await TestHelpers.GetEventTypesAsync(Orchestrator, runId);
        Assert.That(eventTypes, Contains.Item("ExecutionFailed"), "Should have ExecutionFailed event");
    }

    [Test]
    public async Task OnExecutionCompleted_WithoutStarted_FromDispatching_TransitionsToTerminal()
    {
        // Arrange: create and approve run (now in Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(Parser, createMsg);
        var createResult = await Orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(Parser, approveMsg);
        await Orchestrator.HandleInboundAsync(approveMsg, approveIntent);

        // Verify we're in Dispatching
        var timelineBefore = await Orchestrator.GetTimelineAsync(runId);
        Assert.That(timelineBefore.Run.Status, Is.EqualTo(RunStatus.Dispatching));

        // Act: complete without started (robustness: handle missed started event)
        var completionResult = await Orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: true, summary: "ok");

        // Assert: transitions to terminal state
        var timeline = await Orchestrator.GetTimelineAsync(runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Should transition to Succeeded");
            Assert.That(completionResult.Outbound, Has.Count.EqualTo(1), "Should emit completion message");
        });

        var eventTypes = await TestHelpers.GetEventTypesAsync(Orchestrator, runId);
        Assert.Multiple(() =>
        {
            Assert.That(eventTypes, Contains.Item("ExecutionDispatched"));
            Assert.That(eventTypes, Contains.Item("ExecutionSucceeded"));
            // ExecutionStarted may be absent (that's OK for robustness)
        });
    }

    [Test]
    public async Task OnExecutionStarted_UnknownRunId_ReturnsErrorOutbound()
    {
        // Act
        var result = await Orchestrator.OnExecutionStartedAsync("NOPE", "worker-1");

        // Assert: returns error message, no exception
        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo("NOPE"));
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("unknown run").IgnoreCase, "Error message should mention unknown run");
        });
    }

    [Test]
    public async Task OnExecutionCompleted_UnknownRunId_ReturnsErrorOutbound()
    {
        // Act
        var result = await Orchestrator.OnExecutionCompletedAsync("NOPE", "worker-1", success: true, summary: "ok");

        // Assert: returns error message, no exception
        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo("NOPE"));
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("unknown run").IgnoreCase, "Error message should mention unknown run");
        });
    }

    [Test]
    public async Task OnExecutionCompleted_InvalidState_ReturnsErrorOutbound()
    {
        // Arrange: create run (AwaitingApproval, not Running/Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(Parser, createMsg);
        var createResult = await Orchestrator.HandleInboundAsync(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        // Act: try to complete from AwaitingApproval
        var result = await Orchestrator.OnExecutionCompletedAsync(runId, "worker-1", success: true, summary: "ok");

        // Assert: error message, state unchanged
        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("Cannot complete"));
        });

        var timeline = await Orchestrator.GetTimelineAsync(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.AwaitingApproval), "State should remain unchanged");
    }
}
