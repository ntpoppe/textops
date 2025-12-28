using TextOps.Contracts.Runs;

namespace TextOps.Persistence.Entities;

/// <summary>
/// Database entity representing a run.
/// </summary>
public sealed class RunEntity
{
    public string RunId { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string RequestedByAddress { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;

    public ICollection<RunEventEntity> Events { get; set; } = new List<RunEventEntity>();

    public Run ToRun() => new(
        RunId: RunId,
        JobKey: JobKey,
        Status: Status,
        CreatedAt: CreatedAt,
        RequestedByAddress: RequestedByAddress,
        ChannelId: ChannelId,
        ConversationId: ConversationId
    );

    public static RunEntity FromRun(Run run) => new()
    {
        RunId = run.RunId,
        JobKey = run.JobKey,
        Status = run.Status,
        CreatedAt = run.CreatedAt,
        RequestedByAddress = run.RequestedByAddress,
        ChannelId = run.ChannelId,
        ConversationId = run.ConversationId,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };
}

