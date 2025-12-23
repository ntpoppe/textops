using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TextOps.Channels.DevApi.Dtos;

namespace TextOps.Channels.DevApi.Tests;

[TestFixture]
public sealed class DevInboundIntegrationTests
{
    private DevApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private JsonSerializerOptions _jsonOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new DevApiWebApplicationFactory();
        _client = _factory.CreateClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task HandleInbound_RunJob_HappyPath_Returns200WithRunIdAndApprovalPrompt()
    {
        // Arrange
        var request = new DevInboundRequest
        {
            From = "user1",
            Conversation = "user1",
            Body = "run demo",
            ProviderMessageId = $"happy-path-{Guid.NewGuid()}"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/dev/inbound", request, _jsonOptions);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Response should be 200 OK");
        });

        var content = await response.Content.ReadFromJsonAsync<DevInboundResponse>(_jsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(content, Is.Not.Null, "Response body should not be null");
            Assert.That(content!.IntentType, Is.EqualTo("RunJob"), "Intent type should be RunJob");
            Assert.That(content.JobKey, Is.EqualTo("demo"), "Job key should be 'demo'");
            Assert.That(content.RunId, Is.Not.Null.And.Not.Empty, "Run ID should be present");
            Assert.That(content.Outbound, Is.Not.Empty, "Outbound messages should be present");
            Assert.That(content.Outbound[0].Body, Does.Contain("approve"), "Outbound message should contain approval prompt");
        });
    }

    [Test]
    public async Task HandleInbound_SameProviderMessageIdTwice_SecondCallHasNoOutbound()
    {
        // Arrange
        var providerMessageId = $"idempotency-{Guid.NewGuid()}";
        var request = new DevInboundRequest
        {
            From = "user1",
            Conversation = "user1",
            Body = "run demo",
            ProviderMessageId = providerMessageId
        };

        // Act - First call
        var firstResponse = await _client.PostAsJsonAsync("/dev/inbound", request, _jsonOptions);
        var firstContent = await firstResponse.Content.ReadFromJsonAsync<DevInboundResponse>(_jsonOptions);

        // Act - Second call with same providerMessageId
        var secondResponse = await _client.PostAsJsonAsync("/dev/inbound", request, _jsonOptions);
        var secondContent = await secondResponse.Content.ReadFromJsonAsync<DevInboundResponse>(_jsonOptions);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), "First call should succeed");
            Assert.That(firstContent, Is.Not.Null, "First response body should not be null");
            Assert.That(firstContent!.Outbound, Is.Not.Empty, "First call should have outbound messages");
            Assert.That(firstContent.RunId, Is.Not.Null.And.Not.Empty, "First call should create a run");

            Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Second call should succeed");
            Assert.That(secondContent, Is.Not.Null, "Second response body should not be null");
            Assert.That(secondContent!.Outbound, Is.Empty, "Second call should have no outbound messages (idempotency)");
        });
    }

    [Test]
    public async Task HandleInbound_RunJob_ThenGetTimeline_ReturnsRunWithRunCreatedEvent()
    {
        // Arrange
        var request = new DevInboundRequest
        {
            From = "user1",
            Conversation = "user1",
            Body = "run demo",
            ProviderMessageId = $"timeline-{Guid.NewGuid()}"
        };

        // Act - Create run
        var createResponse = await _client.PostAsJsonAsync("/dev/inbound", request, _jsonOptions);
        var createContent = await createResponse.Content.ReadFromJsonAsync<DevInboundResponse>(_jsonOptions);

        // Act - Get timeline
        var timelineResponse = await _client.GetAsync($"/runs/{createContent!.RunId}");
        var timelineContent = await timelineResponse.Content.ReadFromJsonAsync<TimelineResponse>(_jsonOptions);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(timelineResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Timeline request should succeed");
            Assert.That(timelineContent, Is.Not.Null, "Timeline response should not be null");
            Assert.That(timelineContent!.Run.RunId, Is.EqualTo(createContent.RunId), "Run ID should match");
            Assert.That(timelineContent.Events, Is.Not.Empty, "Events should be present");
            Assert.That(timelineContent.Events.Any(e => e.Type == "RunCreated"), Is.True, "Events should include RunCreated");
        });
    }

    [Test]
    [TestCase("", "user1", "run demo", "from")]
    [TestCase("user1", "", "run demo", "conversation")]
    [TestCase("user1", "user1", "", "body")]
    [TestCase("   ", "user1", "run demo", "from")]
    [TestCase("user1", "   ", "run demo", "conversation")]
    [TestCase("user1", "user1", "   ", "body")]
    public async Task HandleInbound_MissingOrBlankRequiredField_Returns400(
        string from,
        string conversation,
        string body,
        string expectedField)
    {
        // Arrange
        var request = new DevInboundRequest
        {
            From = from,
            Conversation = conversation,
            Body = body,
            ProviderMessageId = $"validation-{Guid.NewGuid()}"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/dev/inbound", request, _jsonOptions);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "Should return 400 Bad Request");
        });

        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(problemDetails.TryGetProperty("detail", out var detail), Is.True, "Problem details should include detail");
            var detailString = detail.GetString();
            Assert.That(detailString, Is.Not.Null, "Detail string should not be null");
            Assert.That(detailString!, Does.Contain(expectedField).IgnoreCase, $"Detail should mention '{expectedField}'");
        });
    }
}

