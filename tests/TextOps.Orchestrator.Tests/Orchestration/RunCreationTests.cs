using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class RunCreationTests : OrchestratorTestBase
{
    [Test]
    public void HandleInbound_RunJob_CreatesRunWithAwaitingApprovalStatus()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "run demo", providerMessageId: $"create-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = Orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.Not.Null);
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.DispatchedExecution, Is.False);
        });

        var timeline = Orchestrator.GetTimeline(result.RunId!);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Run.JobKey, Is.EqualTo("demo"));
            Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.AwaitingApproval));
            Assert.That(timeline.Run.RequestedByAddress, Is.EqualTo(msg.From.Value));
            Assert.That(timeline.Run.ChannelId, Is.EqualTo(msg.ChannelId));
        });
    }

    [Test]
    public void HandleInbound_RunJob_ReturnsApprovalPrompt()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "run demo", providerMessageId: $"create-prompt-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = Orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one outbound message");
            var outbound = result.Outbound[0];
            Assert.That(outbound.Body, Does.Contain("ready"), "Message should indicate job is ready");
            Assert.That(outbound.Body, Does.Contain("YES"), "Message should contain approval instruction");
            Assert.That(outbound.Body, Does.Contain("NO"), "Message should contain denial instruction");
            Assert.That(outbound.Body, Does.Contain(result.RunId), "Message should contain run ID");
            Assert.That(outbound.CorrelationId, Is.EqualTo(result.RunId), "Correlation ID should match run ID");
        });
    }

    [Test]
    public void HandleInbound_RunJob_AppendsRunCreatedAndApprovalRequestedEvents()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "run demo", providerMessageId: $"create-events-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = Orchestrator.HandleInbound(msg, intent);

        var timeline = Orchestrator.GetTimeline(result.RunId!);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.Events, Has.Count.EqualTo(2));
            Assert.That(timeline.Events[0].Type, Is.EqualTo("RunCreated"));
            Assert.That(timeline.Events[1].Type, Is.EqualTo("ApprovalRequested"));
            Assert.That(timeline.Events[0].Actor, Does.Contain("user:"));
            Assert.That(timeline.Events[1].Actor, Is.EqualTo("system"));
        });
    }

    [Test]
    public void HandleInbound_RunJobWithMissingJobKey_ReturnsError()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "run", providerMessageId: $"create-error-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = Orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.Outbound, Has.Count.EqualTo(1));
            Assert.That(result.Outbound[0].Body, Does.Contain("Missing job key"));
            Assert.That(result.DispatchedExecution, Is.False);
        });
    }
}
