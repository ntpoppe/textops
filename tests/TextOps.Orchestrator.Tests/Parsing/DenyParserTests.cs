using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Parsing;

[TestFixture]
public class DenyParserTests
{
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new DeterministicIntentParser();
    }

    [Test]
    [TestCase("no ABC123", "ABC123")]
    [TestCase("no 8f3a", "8f3a")]
    [TestCase("deny ABC123", "ABC123")]
    [TestCase("deny 8f3a", "8f3a")]
    [TestCase("NO ABC123", "ABC123")] // case insensitive
    [TestCase("DENY ABC123", "ABC123")]
    [TestCase("No Test", "Test")]
    public void Parse_ValidDenyCommand_ReturnsDenyRunIntent(string input, string expectedRunId)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.DenyRun));
            Assert.That(result.RunId, Is.EqualTo(expectedRunId));
            Assert.That(result.JobKey, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }

    [Test]
    [TestCase("no")]
    [TestCase("no ")]
    [TestCase("deny")]
    [TestCase("deny ")]
    [TestCase("not ABC123")]
    [TestCase("denial ABC123")]
    [TestCase("ABC123 no")]
    [TestCase("no-ABC123")]
    [TestCase("no ABC123 extra")]
    public void Parse_InvalidDenyCommand_ReturnsUnknown(string input)
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

