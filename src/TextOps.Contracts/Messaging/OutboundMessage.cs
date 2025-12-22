namespace TextOps.Contracts.Messaging;

public sealed record OutboundMessage(
    string ChannelId,
    ConversationId Conversation,
    Address? To,
    string Body,
    string CorrelationId,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string>? ReplyContext = null
);
