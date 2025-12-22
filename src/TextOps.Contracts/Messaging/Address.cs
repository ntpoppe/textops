namespace TextOps.Contracts.Messaging;

/// <summary>
/// Opaque address string. Treated as a URI-like identifier.
/// For example:
///   sms:+15551234567
///   email:user@example.com
/// </summary>
public readonly record struct Address(string Value)
{
    public override string ToString() => Value;
}
