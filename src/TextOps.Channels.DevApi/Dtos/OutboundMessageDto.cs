namespace TextOps.Channels.DevApi.Dtos;

public sealed record OutboundMessageDto
{
    public required string Body { get; init; }
    public required string CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string ChannelId { get; init; }
    public required string Conversation { get; init; }
}

