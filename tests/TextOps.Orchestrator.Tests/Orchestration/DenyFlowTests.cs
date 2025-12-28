using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class DenyFlowTests : OrchestratorTestBase
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
    public async Task HandleInbound_DenyWhenAwaitingApproval_TransitionsToDenied()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        var timeline = await Orchestrator.GetTimelineAsync(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }

    [Test]
    public async Task HandleInbound_DenyWhenAwaitingApproval_EmitsRunDeniedEvent()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-event-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        await Orchestrator.HandleInboundAsync(msg, intent);

        var timeline = await Orchestrator.GetTimelineAsync(_runId);
        var denyEvent = timeline.Events.FirstOrDefault(e => e.Type == "RunDenied");
        Assert.Multiple(() =>
        {
            Assert.That(denyEvent, Is.Not.Null);
            Assert.That(denyEvent!.Actor, Does.Contain("user:"));
        });
    }

    [Test]
    public async Task HandleInbound_DenyWhenAwaitingApproval_ReturnsDenialMessage()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-message-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one outbound message");
            var outbound = result.Outbound[0];
            Assert.That(outbound.Body, Does.Contain("Denied"), "Message should indicate denial");
            Assert.That(outbound.Body, Does.Contain(_runId), "Message should contain run ID");
            Assert.That(result.DispatchedExecution, Is.False, "Should not dispatch execution when denied");
        });
    }

    [Test]
    public async Task HandleInbound_DenyWithDenyKeyword_Works()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"deny {_runId}", providerMessageId: $"deny-keyword-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = await Orchestrator.HandleInboundAsync(msg, intent);

        var timeline = await Orchestrator.GetTimelineAsync(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }
}
