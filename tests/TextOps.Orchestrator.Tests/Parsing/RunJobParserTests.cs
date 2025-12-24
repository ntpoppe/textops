using TextOps.Contracts.Intents;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Parsing;

[TestFixture]
public class RunJobParserTests
{
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _parser = new DeterministicIntentParser();
    }

    [Test]
    [TestCase("run demo", "demo")]
    [TestCase("run nightly-backup", "nightly-backup")]
    [TestCase("run job_123", "job_123")]
    [TestCase("run test-job", "test-job")]
    [TestCase("RUN DEMO", "DEMO")] // case insensitive
    [TestCase("Run Test", "Test")]
    public void Parse_ValidRunCommand_ReturnsRunJobIntent(string input, string expectedJobKey)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.RunJob));
            Assert.That(result.JobKey, Is.EqualTo(expectedJobKey));
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }

    [Test]
    [TestCase("run")]
    [TestCase("run ")]
    [TestCase("run  ")]
    public void Parse_RunCommandWithoutJobKey_ReturnsRunJobWithNullJobKey(string input)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.RunJob), "Should recognize 'run' as RunJob intent");
            Assert.That(result.JobKey, Is.Null, "JobKey should be null when not provided");
            Assert.That(result.RunId, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(input.Trim()));
        });
    }

    [Test]
    [TestCase("runme")]
    [TestCase("running demo")]
    [TestCase("demo run")]
    [TestCase("run-demo")]
    [TestCase("run@demo")]
    [TestCase("run demo extra")]
    public void Parse_InvalidRunCommand_ReturnsUnknown(string input)
    {
        var result = _parser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.Unknown));
            Assert.That(result.JobKey, Is.Null);
            Assert.That(result.RunId, Is.Null);
        });
    }

    [Test]
    public void Parse_RunCommandWithWhitespace_TrimsInput()
    {
        var result = _parser.Parse("  run demo  ");

        Assert.Multiple(() =>
        {
            Assert.That(result.Type, Is.EqualTo(IntentType.RunJob));
            Assert.That(result.JobKey, Is.EqualTo("demo"));
            Assert.That(result.RawText, Is.EqualTo("run demo"));
        });
    }
}

