using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class ApprovalFlowTests : OrchestratorTestBase
{
    private string _runId = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        // Create a run first
        var createMsg = TestHelpers.CreateInboundMessage(body: "run demo", providerMessageId: $"create-{Guid.NewGuid()}");
        var createIntent = Parser.Parse(createMsg.Body);
        var createResult = await Orchestrator.HandleInboundAsync(createMsg, createIntent);
        _runId = createResult.RunId!;
    }

    [Test]
    public async Task HandleInbound_ApproveWhenAwaitingApproval_TransitionsToDispatching()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo(_runId));
            Assert.That(result.DispatchedExecution, Is.True);
        });

        var timeline = await Orchestrator.GetTimelineAsync(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Dispatching));
    }

    [Test]
    public async Task HandleInbound_ApproveWhenAwaitingApproval_EmitsRunApprovedAndExecutionDispatched()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-events-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        var timeline = await Orchestrator.GetTimelineAsync(_runId);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Events, Has.Count.GreaterThanOrEqualTo(4));
            var approveEvent = timeline.Events.FirstOrDefault(e => e.Type == "RunApproved");
            var dispatchEvent = timeline.Events.FirstOrDefault(e => e.Type == "ExecutionDispatched");

            Assert.That(approveEvent, Is.Not.Null);
            Assert.That(dispatchEvent, Is.Not.Null);
            Assert.That(approveEvent!.Actor, Does.Contain("user:"));
            Assert.That(dispatchEvent!.Actor, Is.EqualTo("system"));
        });
    }

    [Test]
    public async Task HandleInbound_ApproveWhenAwaitingApproval_ReturnsConfirmationMessage()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"yes {_runId}", providerMessageId: $"approve-confirm-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one outbound message");
            var outbound = result.Outbound[0];
            Assert.That(outbound.Body, Does.Contain("Approved"), "Message should indicate approval");
            Assert.That(outbound.Body, Does.Contain("Starting"), "Message should indicate execution starting");
            Assert.That(outbound.Body, Does.Contain(_runId), "Message should contain run ID");
            Assert.That(outbound.CorrelationId, Is.EqualTo(_runId), "Correlation ID should match run ID");
        });
    }

    [Test]
    public async Task HandleInbound_ApproveWithApprovalKeyword_Works()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"approve {_runId}", providerMessageId: $"approve-keyword-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.EqualTo(_runId));
            Assert.That(result.DispatchedExecution, Is.True);
        });
    }
}
