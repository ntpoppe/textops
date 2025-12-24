using TextOps.Contracts.Intents;
using TextOps.Contracts.Parsing;
using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class InvalidTransitionTests
{
    private InMemoryRunOrchestrator _orchestrator = null!;
    private DeterministicIntentParser _parser = null!;
    private string _runId = null!;

    [SetUp]
    public void SetUp()
    {
        _orchestrator = new InMemoryRunOrchestrator();
        _parser = new DeterministicIntentParser();

        // Create a run first
        var createMsg = TestHelpers.CreateInboundMessage(body: "run demo", providerMessageId: $"create-{Guid.NewGuid()}");
        var createIntent = _parser.Parse(createMsg.Body);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        _runId = createResult.RunId!;
    }

    [Test]
    public void HandleInbound_ApproveTwice_SecondIsRejected()
    {
        // Arrange: Approve the run first
        var approveMsg1 = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-first-{Guid.NewGuid()}");
        var approveIntent1 = _parser.Parse(approveMsg1.Body);
        _orchestrator.HandleInbound(approveMsg1, approveIntent1);

        // Act: Attempt to approve again
        var approveMsg2 = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-second-{Guid.NewGuid()}");
        var approveIntent2 = _parser.Parse(approveMsg2.Body);
        var result2 = _orchestrator.HandleInbound(approveMsg2, approveIntent2);

        // Assert: Second approval should be rejected
        Assert.Multiple(() =>
        {
            Assert.That(result2.Outbound, Has.Count.EqualTo(1), "Should return one error message");
            Assert.That(result2.Outbound[0].Body, Does.Contain("Cannot approve"), "Error should indicate approval not allowed");
            Assert.That(result2.DispatchedExecution, Is.False, "Should not dispatch execution");
        });

        // Verify run remains in Dispatching state
        var timeline = _orchestrator.GetTimeline(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Dispatching), "Run should remain in Dispatching state");
    }

    [Test]
    public void HandleInbound_DenyAfterApprove_Rejected()
    {
        // Arrange: Approve the run first
        var approveMsg = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-{Guid.NewGuid()}");
        var approveIntent = _parser.Parse(approveMsg.Body);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        // Act: Try to deny after approval
        var denyMsg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-after-approve-{Guid.NewGuid()}");
        var denyIntent = _parser.Parse(denyMsg.Body);
        var result = _orchestrator.HandleInbound(denyMsg, denyIntent);

        // Assert: Denial should be rejected
        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one error message");
            Assert.That(result.Outbound[0].Body, Does.Contain("Cannot deny"), "Error should indicate denial not allowed");
            Assert.That(result.DispatchedExecution, Is.False, "Should not dispatch execution");
        });

        // Verify run remains in Dispatching state
        var timeline = _orchestrator.GetTimeline(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Dispatching), "Run should remain in Dispatching state");
    }

    [Test]
    public void HandleInbound_ApproveUnknownRun_Rejected()
    {
        // Arrange
        var msg = TestHelpers.CreateInboundMessage(body: "yes UNKNOWN123", providerMessageId: $"approve-unknown-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        // Act
        var result = _orchestrator.HandleInbound(msg, intent);

        // Assert: Should reject approval of unknown run
        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.Null, "Should not return a run ID for unknown run");
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one error message");
            Assert.That(result.Outbound[0].Body, Does.Contain("Unknown run id"), "Error should indicate unknown run");
            Assert.That(result.DispatchedExecution, Is.False, "Should not dispatch execution for error");
        });
    }

    [Test]
    public void HandleInbound_DenyAfterDeny_Rejected()
    {
        // Arrange: Deny the run first
        var denyMsg1 = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-first-{Guid.NewGuid()}");
        var denyIntent1 = _parser.Parse(denyMsg1.Body);
        _orchestrator.HandleInbound(denyMsg1, denyIntent1);

        // Act: Try to deny again
        var denyMsg2 = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-second-{Guid.NewGuid()}");
        var denyIntent2 = _parser.Parse(denyMsg2.Body);
        var result = _orchestrator.HandleInbound(denyMsg2, denyIntent2);

        // Assert: Second denial should be rejected
        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one error message");
            Assert.That(result.Outbound[0].Body, Does.Contain("Cannot deny"), "Error should indicate denial not allowed");
            Assert.That(result.DispatchedExecution, Is.False, "Should not dispatch execution");
        });
    }
}

