namespace TextOps.Contracts.Messaging;

/// <summary>
/// Stable identifier for a conversation/thread.
/// For example:
///   sms:+15551234567
///   slack:channel:C123/thread:1700000000.0001
/// </summary>
public readonly record struct ConversationId(string Value)
{
    public override string ToString() => Value;
}
