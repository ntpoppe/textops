using TextOps.Contracts.Intents;
using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class DenyFlowTests
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
    public void HandleInbound_DenyWhenAwaitingApproval_TransitionsToDenied()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

        var timeline = _orchestrator.GetTimeline(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }

    [Test]
    public void HandleInbound_DenyWhenAwaitingApproval_EmitsRunDeniedEvent()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-event-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        _orchestrator.HandleInbound(msg, intent);

        var timeline = _orchestrator.GetTimeline(_runId);
        var denyEvent = timeline.Events.FirstOrDefault(e => e.Type == "RunDenied");
        Assert.Multiple(() =>
        {
            Assert.That(denyEvent, Is.Not.Null);
            Assert.That(denyEvent!.Actor, Does.Contain("user:"));
        });
    }

    [Test]
    public void HandleInbound_DenyWhenAwaitingApproval_ReturnsDenialMessage()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"no {_runId}", providerMessageId: $"deny-message-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

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
    public void HandleInbound_DenyWithDenyKeyword_Works()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"deny {_runId}", providerMessageId: $"deny-keyword-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

        var timeline = _orchestrator.GetTimeline(_runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));
    }
}

