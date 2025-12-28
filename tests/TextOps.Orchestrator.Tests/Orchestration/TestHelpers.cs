using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

internal static class TestHelpers
{
    public static InboundMessage CreateInboundMessage(
        string body,
        string? providerMessageId = null,
        string from = "dev:user1",
        string conversation = "dev:conv:user1",
        string channelId = "dev")
    {
        return new InboundMessage(
            ChannelId: channelId,
            ProviderMessageId: providerMessageId ?? Guid.NewGuid().ToString("n"),
            Conversation: new ConversationId(conversation),
            From: new Address(from),
            To: null,
            Body: body,
            ReceivedAt: DateTimeOffset.UtcNow,
            ProviderMeta: null
        );
    }

    public static ParsedIntent Parse(IIntentParser parser, InboundMessage inbound)
    {
        return parser.Parse(inbound.Body);
    }

    public static string ExtractRunIdFromResult(OrchestratorResult result)
    {
        if (result.RunId == null)
            throw new InvalidOperationException("Result has no RunId");
        return result.RunId;
    }

    public static async Task<string[]> GetEventTypesAsync(IRunOrchestrator orchestrator, string runId)
    {
        var timeline = await orchestrator.GetTimelineAsync(runId);
        return timeline.Events.Select(e => e.Type).ToArray();
    }
}

