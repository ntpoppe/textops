namespace TextOps.Channels.DevApi.Dtos;

public sealed record RunEventDto
{
    public required string RunId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Actor { get; init; }
    public required object Payload { get; init; }
}

