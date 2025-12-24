namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class UnknownIntentTests : OrchestratorTestBase
{
    [Test]
    public void HandleInbound_UnknownIntent_ReturnsHelpMessage()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "junk input", providerMessageId: $"unknown-{Guid.NewGuid()}");
        var intent = Parser.Parse(msg.Body);

        var result = Orchestrator.HandleInbound(msg, intent);

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
