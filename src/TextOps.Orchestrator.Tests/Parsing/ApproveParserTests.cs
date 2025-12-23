using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Parsing;

[TestFixture]
public class ApproveParserTests
{
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new DeterministicIntentParser();
    }

    [Test]
    [TestCase("yes ABC123", "ABC123")]
    [TestCase("yes 8f3a", "8f3a")]
    [TestCase("yes run-123", "run-123")]
    [TestCase("approve ABC123", "ABC123")]
    [TestCase("approve 8f3a", "8f3a")]
    [TestCase("YES ABC123", "ABC123")] // case insensitive
    [TestCase("APPROVE ABC123", "ABC123")]
    [TestCase("Yes Test", "Test")]
    public void Parse_ValidApproveCommand_ReturnsApproveRunIntent(string input, string expectedRunId)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.ApproveRun));
            Assert.That(result.RunId, Is.EqualTo(expectedRunId));
            Assert.That(result.JobKey, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }

    [Test]
    [TestCase("yes")]
    [TestCase("yes ")]
    [TestCase("approve")]
    [TestCase("approve ")]
    [TestCase("yesly")]
    [TestCase("approval ABC123")]
    [TestCase("ABC123 yes")]
    [TestCase("yes-ABC123")]
    [TestCase("yes ABC123 extra")]
    public void Parse_InvalidApproveCommand_ReturnsUnknown(string input)
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

