namespace TextOps.Contracts.Runs;

public sealed record Run(
    string RunId,
    string JobKey,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    string RequestedByAddress,
    string ChannelId,
    string ConversationId
);
