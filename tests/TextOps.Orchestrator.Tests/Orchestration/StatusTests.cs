using TextOps.Contracts.Intents;
using TextOps.Contracts.Parsing;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

[TestFixture]
public class StatusTests
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
    public void HandleInbound_Status_ReturnsRunInformation()
    {
        var msg = TestHelpers.CreateInboundMessage(body: $"status {_runId}", providerMessageId: $"status-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one outbound message");
            var outbound = result.Outbound[0];
            Assert.That(outbound.Body, Does.Contain(_runId), "Status should contain run ID");
            Assert.That(outbound.Body, Does.Contain("demo"), "Status should contain job key");
            Assert.That(outbound.Body, Does.Contain("State"), "Status should contain state information");
            Assert.That(result.DispatchedExecution, Is.False, "Status query should not dispatch execution");
        });
    }

    [Test]
    public void HandleInbound_StatusUnknownRun_ReturnsError()
    {
        var msg = TestHelpers.CreateInboundMessage(body: "status UNKNOWN123", providerMessageId: $"status-unknown-{Guid.NewGuid()}");
        var intent = _parser.Parse(msg.Body);

        var result = _orchestrator.HandleInbound(msg, intent);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunId, Is.Null, "Should not return a run ID for unknown run");
            Assert.That(result.Outbound, Has.Count.EqualTo(1), "Should return one error message");
            Assert.That(result.Outbound[0].Body, Does.Contain("Unknown run id"), "Error message should indicate unknown run");
            Assert.That(result.DispatchedExecution, Is.False, "Should not dispatch execution for error");
        });
    }
}

