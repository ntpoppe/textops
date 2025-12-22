namespace TextOps.Contracts.Messaging;

/// <summary>
/// Logical identifier for the transport, not the provider SDK.
/// </summary>
/// <remarks>
/// <para>
/// Strings keep the core open-ended. Adapters translate from provider → channelId.
/// </para>
/// <para>
/// Examples:
/// </para>
/// <list type="bullet">
/// <item><description>"dev" → local fake gateway</description></item>
/// <item><description>"twilio.sms" → Twilio SMS adapter</description></item>
/// </list>
/// </remarks>
public static class ChannelIds
{
    /// <summary>
    /// Local fake gateway for development and testing.
    /// </summary>
    public const string Dev = "dev";

    /// <summary>
    /// Twilio SMS adapter channel identifier.
    /// </summary>
    public const string TwilioSms = "twilio.sms";
} 