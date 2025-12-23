using TextOps.Contracts.Messaging;

namespace TextOps.Orchestrator.Tests.Orchestration;

internal static class TestHelpers
{
    public static InboundMessage CreateInboundMessage(
        string channelId = "dev",
        string providerMessageId = "msg-1",
        string body = "test",
        Address? from = null,
        ConversationId? conversation = null)
    {
        return new InboundMessage(
            ChannelId: channelId,
            ProviderMessageId: providerMessageId,
            Conversation: conversation ?? new ConversationId("conv-1"),
            From: from ?? new Address("sms:+15551234567"),
            To: null,
            Body: body,
            ReceivedAt: DateTimeOffset.UtcNow,
            ProviderMeta: null
        );
    }
}

