namespace TextOps.Contracts.Runs;

/// <summary>
/// Represents a job run with its current state and routing information.
/// </summary>
/// <remarks>
/// This is the authoritative snapshot of a run's current state. The status is
/// derived from events, but stored here for fast access. This record contains
/// all the information needed to continue interactions, send notifications, and
/// route replies back to the correct conversation.
/// </remarks>
/// <param name="RunId">
/// Stable identifier referenced everywhere. This is the primary key for the run.
/// </param>
/// <param name="JobKey">
/// Which job is being run. Eventually resolves to a Job Catalog entry.
/// </param>
/// <param name="Status">
/// Current state snapshot. Derived from events, but stored for fast access.
/// </param>
/// <param name="CreatedAt">
/// When the run was created. Used for audit and timeout calculations.
/// </param>
/// <param name="RequestedByAddress">
/// Who initiated it (string form of Address). Stored as string to avoid versioning
/// issues and keep persistence simple.
/// </param>
/// <param name="ChannelId">
/// Which channel owns the conversation. Ensures replies go back the right way.
/// </param>
/// <param name="ConversationId">
/// Where to continue the interaction. This is what lets approval prompts, completion
/// notifications, retries, and all other interactions land in the same place.
/// </param>
public sealed record Run(
    string RunId,
    string JobKey,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    string RequestedByAddress,
    string ChannelId,
    string ConversationId
);
