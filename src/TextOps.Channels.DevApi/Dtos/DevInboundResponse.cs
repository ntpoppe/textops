namespace TextOps.Channels.DevApi.Dtos;

public sealed record DevInboundResponse
{
    public required string IntentType { get; init; }
    public string? JobKey { get; init; }
    public string? RunId { get; init; }
    public required bool DispatchedExecution { get; init; }
    public required IReadOnlyList<OutboundMessageDto> Outbound { get; init; }
}

