using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Parsing;

[TestFixture]
public class StatusParserTests
{
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new DeterministicIntentParser();
    }

    [Test]
    [TestCase("status ABC123", "ABC123")]
    [TestCase("status 8f3a", "8f3a")]
    [TestCase("status run-123", "run-123")]
    [TestCase("STATUS ABC123", "ABC123")] // case insensitive
    [TestCase("Status Test", "Test")]
    public void Parse_ValidStatusCommand_ReturnsStatusIntent(string input, string expectedRunId)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.Status));
            Assert.That(result.RunId, Is.EqualTo(expectedRunId));
            Assert.That(result.JobKey, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }

    [Test]
    [TestCase("status")]
    [TestCase("status ")]
    [TestCase("statusly ABC123")]
    [TestCase("ABC123 status")]
    [TestCase("status-ABC123")]
    [TestCase("status ABC123 extra")]
    public void Parse_InvalidStatusCommand_ReturnsUnknown(string input)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.Unknown));
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.JobKey, Is.Null);
        });
    }
}

