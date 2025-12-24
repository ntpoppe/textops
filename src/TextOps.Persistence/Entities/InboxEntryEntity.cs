namespace TextOps.Persistence.Entities;

/// <summary>
/// Database entity for inbound message deduplication.
/// </summary>
public sealed class InboxEntryEntity
{
    public string ChannelId { get; set; } = string.Empty;
    public string ProviderMessageId { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
    public string? RunId { get; set; }
}

