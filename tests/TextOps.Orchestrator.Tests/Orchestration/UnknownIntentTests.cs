using TextOps.Contracts.Intents;
using TextOps.Contracts.Parsing;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class UnknownIntentTests
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
    public void HandleInbound_UnknownIntent_ReturnsHelpMessage()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "junk input", providerMessageId: $"unknown-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

        var outbound = result.Outbound[0];
        Assert.Multiple(() =>
        {
            Assert.That(outbound.Body, Does.Contain("Unknown command"));
            Assert.That(outbound.Body, Does.Contain("run"));
            Assert.That(outbound.Body, Does.Contain("yes"));
            Assert.That(outbound.Body, Does.Contain("no"));
            Assert.That(outbound.Body, Does.Contain("status"));
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.DispatchedExecution, Is.False);
        });
    }
}

