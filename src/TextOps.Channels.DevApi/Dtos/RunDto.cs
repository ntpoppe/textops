namespace TextOps.Channels.DevApi.Dtos;

public sealed record RunDto
{
    public required string RunId { get; init; }
    public required string JobKey { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string RequestedByAddress { get; init; }
    public required string ChannelId { get; init; }
    public required string ConversationId { get; init; }
}

