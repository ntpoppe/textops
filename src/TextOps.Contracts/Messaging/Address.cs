namespace TextOps.Contracts.Messaging;

/// <summary>
/// An opaque identity on a channel.
/// </summary>
/// <remarks>
/// <para>
/// The core system never parses this string. This prevents channel leakage
/// into the orchestrator.
/// </para>
/// <para>
/// Examples:
/// </para>
/// <list type="bullet">
/// <item><description>sms:+15551234567</description></item>
/// <item><description>email:name@domain.com</description></item>
/// </list>
/// </remarks>
/// <param name="Value">The opaque address value as a string.</param>
public readonly record struct Address(string Value)
{
    /// <summary>
    /// Returns the string representation of the address.
    /// </summary>
    /// <returns>The address value.</returns>
    public override string ToString() => Value;
}
