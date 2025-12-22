namespace TextOps.Contracts.Runs;

/// <summary>
/// An append-only fact representing an event that occurred in a run's lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here ever changes. Events are immutable
/// and represent the complete audit trail of what happened to a run.
/// </para>
/// <para>
/// The payload is stored as object so I can evolve event types without schema hell.
/// </para>
/// </remarks>
/// <param name="RunId">
/// The run this event belongs to. Links the event to its run.
/// </param>
/// <param name="Type">
/// What happened. Examples: "RunCreated", "ApprovalRequested", "Approved",
/// "ExecutionStarted", "ExecutionSucceeded".
/// </param>
/// <param name="At">
/// When the event occurred. Used for audit trails and timeline reconstruction.
/// </param>
/// <param name="Actor">
/// Who caused it. Examples: "user:sms:+1555...", "system", "execution".
/// Critical for auditability.
/// </param>
/// <param name="Payload">
/// Structured details. Examples: approval source, error messages, execution metadata.
/// Stored as object so I can evolve event types without schema hell.
/// </param>
public sealed record RunEvent(
    string RunId,
    string Type,
    DateTimeOffset At,
    string Actor,
    object Payload
);
