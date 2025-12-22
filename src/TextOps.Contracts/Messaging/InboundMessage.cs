namespace TextOps.Contracts.Messaging;

/// <summary>
/// Represents an inbound message received from a channel adapter.
/// </summary>
/// <remarks>
/// This is the normalized representation of a message received from any channel.
/// All channel-specific details are abstracted away, leaving only what the
/// orchestrator needs to process the message.
/// </remarks>
/// <param name="ChannelId">
/// Which adapter produced this message. Used for routing replies, deduplication
/// namespace, and audit. Examples: "dev", "twilio.sms", "telegram".
/// </param>
/// <param name="ProviderMessageId">
/// The provider's unique ID for this message. Examples: Twilio MessageSid,
/// Telegram update_id, Slack event_id. This is my idempotency key for inbound
/// messages. If the same webhook arrives twice with the same ChannelId and
/// ProviderMessageId, I can safely ignore it.
/// </param>
/// <param name="Conversation">
/// Where replies should go. This is how the orchestrator stays stateless about
/// channel mechanics.
/// </param>
/// <param name="From">
/// Who sent the message. This is the user identity at the edge. Later, I map
/// this to a UserId or enforce permissions.
/// </param>
/// <param name="To">
/// Who the message was sent to. Nullable because some platforms don't expose a
/// meaningful "to". Often unused in core logic, mostly for audit/debugging.
/// </param>
/// <param name="Body">
/// The raw text content. The only thing the parser cares about.
/// </param>
/// <param name="ReceivedAt">
/// When the platform received it. Used for auditing, timeouts, and replay/debugging.
/// </param>
/// <param name="ProviderMeta">
/// Extra provider-specific data (escape hatch). Examples: Slack thread timestamp,
/// Telegram message type, Twilio webhook fields. Rule: Core logic must not depend
/// on this. Adapters may.
/// </param>
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
