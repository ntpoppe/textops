namespace TextOps.Channels.DevApi.Dtos;

public sealed record TimelineResponse
{
    public required RunDto Run { get; init; }
    public required IReadOnlyList<RunEventDto> Events { get; init; }
}
