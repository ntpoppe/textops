using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class IdempotencyTests
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
    public void HandleInbound_SameChannelIdAndProviderMessageIdTwice_SecondProducesNoEffects()
    {
        var msg = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "msg-123",
            body: "run demo");

        var intent = _parser.Parse(msg.Body);

        // First call
        var result1 = _orchestrator.HandleInbound(msg, intent);
        var runId1 = result1.RunId;
        var eventCount1 = _orchestrator.GetTimeline(runId1!).Events.Count;

        // Second call with same (ChannelId, ProviderMessageId)
        var result2 = _orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result2.RunId, Is.Null);
            Assert.That(result2.Outbound, Is.Empty);
            Assert.That(result2.DispatchedExecution, Is.False);
        });

        // Verify no duplicate events
        var timeline = _orchestrator.GetTimeline(runId1!);
        Assert.That(timeline.Events.Count, Is.EqualTo(eventCount1));
    }

    [Test]
    public void HandleInbound_SameChannelIdAndProviderMessageIdTwice_NoDuplicateRuns()
    {
        var msg = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "msg-123",
            body: "run demo");

        var intent = _parser.Parse(msg.Body);

        // First call
        var result1 = _orchestrator.HandleInbound(msg, intent);
        var runId1 = result1.RunId;

        // Second call with same (ChannelId, ProviderMessageId)
        var result2 = _orchestrator.HandleInbound(msg, intent);

        Assert.That(result2.RunId, Is.Null);

        // Verify only one run exists
        var timeline = _orchestrator.GetTimeline(runId1!);
        Assert.That(timeline.Run.JobKey, Is.EqualTo("demo"));
    }

    [Test]
    public void HandleInbound_DifferentProviderMessageId_SameChannelId_ProcessesNormally()
    {
        var msg1 = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "msg-1",
            body: "run demo");

        var msg2 = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "msg-2",
            body: "run demo");

        var intent1 = _parser.Parse(msg1.Body);
        var intent2 = _parser.Parse(msg2.Body);

        var result1 = _orchestrator.HandleInbound(msg1, intent1);
        var result2 = _orchestrator.HandleInbound(msg2, intent2);

        Assert.Multiple(() =>
        {
            Assert.That(result1.RunId, Is.Not.Null);
            Assert.That(result2.RunId, Is.Not.Null);
            Assert.That(result1.RunId, Is.Not.EqualTo(result2.RunId));
        });
    }

    [Test]
    public void HandleInbound_SameProviderMessageId_DifferentChannelId_ProcessesNormally()
    {
        var msg1 = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "msg-123",
            body: "run demo");

        var msg2 = TestHelpers.CreateInboundMessage(
            channelId: "twilio.sms",
            providerMessageId: "msg-123",
            body: "run demo");

        var intent1 = _parser.Parse(msg1.Body);
        var intent2 = _parser.Parse(msg2.Body);

        var result1 = _orchestrator.HandleInbound(msg1, intent1);
        var result2 = _orchestrator.HandleInbound(msg2, intent2);

        Assert.Multiple(() =>
        {
            Assert.That(result1.RunId, Is.Not.Null);
            Assert.That(result2.RunId, Is.Not.Null);
            Assert.That(result1.RunId, Is.Not.EqualTo(result2.RunId));
        });
    }

    [Test]
    public void HandleInbound_ApproveTwiceWithSameMessageId_SecondIsIdempotent()
    {
        // Create a run
        var createMsg = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "create-1",
            body: "run demo");
        var createIntent = _parser.Parse(createMsg.Body);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = createResult.RunId!;

        // Approve with message ID
        var approveMsg = TestHelpers.CreateInboundMessage(
            channelId: "dev",
            providerMessageId: "approve-1",
            body: $"yes {runId}");
        var approveIntent = _parser.Parse(approveMsg.Body);

        // First approval
        var result1 = _orchestrator.HandleInbound(approveMsg, approveIntent);
        var eventCount1 = _orchestrator.GetTimeline(runId).Events.Count;

        // Second approval with same message ID (should be idempotent)
        var result2 = _orchestrator.HandleInbound(approveMsg, approveIntent);

        Assert.Multiple(() =>
        {
            Assert.That(result2.RunId, Is.Null);
            Assert.That(result2.Outbound, Is.Empty);
            Assert.That(result2.DispatchedExecution, Is.False);
        });

        // Verify no duplicate events
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Events.Count, Is.EqualTo(eventCount1));
    }
}

