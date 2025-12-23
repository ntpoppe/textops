using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Parsing;

[TestFixture]
public class UnknownParserTests
{
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new DeterministicIntentParser();
    }

    [Test]
    [TestCase("junk input")]
    [TestCase("hello world")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("random text")]
    [TestCase("help")]
    [TestCase("list")]
    [TestCase("cancel")]
    [TestCase("123")]
    [TestCase("runme")]
    [TestCase("running")]
    [TestCase("yesly")]
    [TestCase("denial")]
    [TestCase("statusly")]
    public void Parse_JunkInput_ReturnsUnknown(string input)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.Unknown));
            Assert.That(result.JobKey, Is.Null);
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }
}

