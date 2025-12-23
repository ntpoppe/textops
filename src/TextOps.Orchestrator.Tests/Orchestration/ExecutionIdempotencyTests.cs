using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public sealed class ExecutionIdempotencyTests
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
    public void OnExecutionStarted_CalledTwice_OnlyOneExecutionStartedEvent()
    {
        // Arrange: create and approve run (now in Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Act: call OnExecutionStarted twice
        var result1 = _orchestrator.OnExecutionStarted(runId, "worker-1");
        var eventTypes1 = TestHelpers.GetEventTypes(_orchestrator, runId);
        var eventCount1 = eventTypes1.Length;

        var result2 = _orchestrator.OnExecutionStarted(runId, "worker-2");

        // Assert: only one ExecutionStarted event, status remains Running
        var timeline = _orchestrator.GetTimeline(runId);
        var eventTypes2 = TestHelpers.GetEventTypes(_orchestrator, runId);

        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running), "Status should be Running after both calls");
            Assert.That(eventTypes2.Count(e => e == "ExecutionStarted"), Is.EqualTo(1), "Should have exactly one ExecutionStarted event");
            Assert.That(eventTypes2.Length, Is.EqualTo(eventCount1), "No new events should be added");
            Assert.That(result2.Outbound, Is.Empty, "Second call should return no outbound messages (idempotent no-op)");
        });
    }

    [Test]
    public void OnExecutionCompleted_CalledTwice_OnlyOneTerminalEvent()
    {
        // Arrange: get run into Running state
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        _orchestrator.OnExecutionStarted(runId, "worker-1");

        // Act: call OnExecutionCompleted twice
        var result1 = _orchestrator.OnExecutionCompleted(runId, success: true, summary: "ok");
        var eventTypes1 = TestHelpers.GetEventTypes(_orchestrator, runId);
        var terminalEventCount1 = eventTypes1.Count(e => e == "ExecutionSucceeded" || e == "ExecutionFailed");

        var result2 = _orchestrator.OnExecutionCompleted(runId, success: true, summary: "ok again");

        // Assert: only one terminal event, status remains terminal
        var timeline = _orchestrator.GetTimeline(runId);
        var eventTypes2 = TestHelpers.GetEventTypes(_orchestrator, runId);
        var terminalEventCount2 = eventTypes2.Count(e => e == "ExecutionSucceeded" || e == "ExecutionFailed");

        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Status should remain Succeeded");
            Assert.That(terminalEventCount2, Is.EqualTo(1), "Should have exactly one terminal event");
            Assert.That(terminalEventCount2, Is.EqualTo(terminalEventCount1), "No new terminal events should be added");
            Assert.That(result2.Outbound, Is.Empty, "Second call should return no outbound messages (idempotent no-op)");
        });
    }

    [Test]
    public void OnExecutionCompleted_CalledTwice_DifferentSuccessValues_FirstWins()
    {
        // Arrange: get run into Running state
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "m2");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        _orchestrator.OnExecutionStarted(runId, "worker-1");

        // Act: complete with success first, then try failure
        _orchestrator.OnExecutionCompleted(runId, success: true, summary: "succeeded");
        var timeline1 = _orchestrator.GetTimeline(runId);

        _orchestrator.OnExecutionCompleted(runId, success: false, summary: "failed");

        // Assert: first completion wins, status remains Succeeded
        var timeline2 = _orchestrator.GetTimeline(runId);
        var eventTypes = TestHelpers.GetEventTypes(_orchestrator, runId);

        Assert.Multiple(() =>
        {
            Assert.That(timeline2.Run.Status, Is.EqualTo(RunStatus.Succeeded), "Status should remain Succeeded (first wins)");
            Assert.That(eventTypes.Count(e => e == "ExecutionSucceeded"), Is.EqualTo(1), "Should have one ExecutionSucceeded");
            Assert.That(eventTypes.Count(e => e == "ExecutionFailed"), Is.EqualTo(0), "Should have no ExecutionFailed");
        });
    }

    [Test]
    public void HandleInbound_RunJobTwice_SameProviderMessageId_SecondIsIdempotent()
    {
        // Arrange: send same message twice
        var msg = TestHelpers.CreateInboundMessage("run demo", "m1");
        var intent = TestHelpers.Parse(_parser, msg);

        // Act: first call
        var result1 = _orchestrator.HandleInbound(msg, intent);
        var runId1 = result1.RunId;
        var eventTypes1 = runId1 != null ? TestHelpers.GetEventTypes(_orchestrator, runId1) : Array.Empty<string>();
        var runCreatedCount1 = eventTypes1.Count(e => e == "RunCreated");

        // Second call with same providerMessageId
        var result2 = _orchestrator.HandleInbound(msg, intent);

        // Assert: second call produces no effects
        Assert.Multiple(() =>
        {
            Assert.That(result2.RunId, Is.Null, "Second call should return null RunId");
            Assert.That(result2.Outbound, Is.Empty, "Second call should return no outbound messages");
        });

        // Verify only one RunCreated event exists
        if (runId1 != null)
        {
            var eventTypes2 = TestHelpers.GetEventTypes(_orchestrator, runId1);
            var runCreatedCount2 = eventTypes2.Count(e => e == "RunCreated");
            Assert.That(runCreatedCount2, Is.EqualTo(1), "Should have exactly one RunCreated event");
            Assert.That(runCreatedCount2, Is.EqualTo(runCreatedCount1), "Event count should not change");
        }
    }
}

