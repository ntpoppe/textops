namespace TextOps.Contracts.Runs;

/// <summary>
/// The authoritative lifecycle state of a run.
/// </summary>
/// <remarks>
/// Current MVP state machine:
///   AwaitingApproval -> Dispatching -> Running -> Succeeded/Failed
///   AwaitingApproval -> Denied
///
/// Reserved statuses for future use:
///   - Created: Initial transient state before approval request (not persisted)
///   - Approved: For multi-stage approval workflows
///   - Canceled: For user-initiated cancellation
///   - TimedOut: For approval/execution timeout policies
/// </remarks>
public enum RunStatus
{
    /// <summary>
    /// Reserved for future use: Initial transient state before approval request.
    /// Currently, runs transition directly to AwaitingApproval on creation.
    /// </summary>
    Created = 0,

    /// <summary>
    /// The run is waiting for approval before execution.
    /// </summary>
    AwaitingApproval = 1,

    /// <summary>
    /// Reserved for future use: Multi-stage approval workflows.
    /// Currently, approval transitions directly to Dispatching.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// The run is being dispatched to the execution engine.
    /// </summary>
    Dispatching = 3,

    /// <summary>
    /// The run is currently executing.
    /// </summary>
    Running = 4,

    /// <summary>
    /// The run completed successfully.
    /// </summary>
    Succeeded = 5,

    /// <summary>
    /// The run failed during execution.
    /// </summary>
    Failed = 6,

    /// <summary>
    /// The run was denied approval and will not execute.
    /// </summary>
    Denied = 7,

    /// <summary>
    /// Reserved for future use: User-initiated cancellation.
    /// </summary>
    Canceled = 8,

    /// <summary>
    /// Reserved for future use: Approval or execution timeout policies.
    /// </summary>
    TimedOut = 9
}
