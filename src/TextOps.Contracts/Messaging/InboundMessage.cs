namespace TextOps.Contracts.Messaging;

public sealed record InboundMessage(
    string ChannelId,
    string ProviderMessageId,
    ConversationId Conversation,
    Address From,
    Address? To,
    string Body,
    DateTimeOffset ReceivedAt,
    IReadOnlyDictionary<string, string>? ProviderMeta = null
);
