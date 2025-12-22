namespace TextOps.Contracts.Messaging;

/// <summary>
/// Represents an outbound message to be sent through a channel adapter.
/// </summary>
/// <remarks>
/// This is the normalized representation of a message to be sent to any channel.
/// All channel-specific routing details are abstracted, with optional escape
/// hatches for adapter-specific needs.
/// </remarks>
/// <param name="ChannelId">
/// Which adapter should send this message.
/// </param>
/// <param name="Conversation">
/// Where this message belongs. Often enough to send a reply without specifying To.
/// </param>
/// <param name="To">
/// Explicit recipient override. Useful for notifications and multi-user messages.
/// Often null for "reply in conversation".
/// </param>
/// <param name="Body">
/// What the human sees. The message content to be delivered.
/// </param>
/// <param name="CorrelationId">
/// Links this message to a run or action. Used for logs, tracing, debugging, and
/// audit. Example: correlationId = runId.
/// </param>
/// <param name="IdempotencyKey">
/// Prevents duplicate outbound sends. If the orchestrator retries, the adapter
/// checks this to avoid sending the same message twice.
/// </param>
/// <param name="ReplyContext">
/// Adapter-only routing hints. Examples: Slack thread timestamp, Telegram reply-to
/// message ID. Same rule as ProviderMeta: core ignores this.
/// </param>
public sealed record OutboundMessage(
    string ChannelId,
    ConversationId Conversation,
    Address? To,
    string Body,
    string CorrelationId,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string>? ReplyContext = null
);
