namespace TextOps.Contracts.Messaging;

/// <summary>
/// A reply routing context.
/// </summary>
/// <remarks>
/// <para>
/// This is critical because messaging is conversational:
/// </para>
/// <list type="bullet">
/// <item><description>SMS replies go back to the sender</description></item>
/// <item><description>Email replies go to a another email</description></item>
/// </list>
/// <para>
/// Without this, replies become impossible to generalize. The orchestrator
/// stores this so every future message knows where to go.
/// </para>
/// <para>
/// Examples:
/// </para>
/// <list type="bullet">
/// <item><description>SMS: sms:+15551234567</description></item>
/// <item><description>Email: email:thread:abc123xyz</description></item>
/// </list>
/// </remarks>
/// <param name="Value">The conversation identifier as a string.</param>
public readonly record struct ConversationId(string Value)
{
    /// <summary>
    /// Returns the string representation of the conversation ID.
    /// </summary>
    /// <returns>The conversation ID value.</returns>
    public override string ToString() => Value;
}
