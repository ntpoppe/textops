namespace TextOps.Contracts.Runs;

/// <summary>
/// The authoritative lifecycle state of a run.
/// </summary>
public enum RunStatus
{
    /// <summary>
    /// The run has been created but not yet submitted for approval.
    /// </summary>
    Created = 0,

    /// <summary>
    /// The run is waiting for approval before execution.
    /// </summary>
    AwaitingApproval = 1,

    /// <summary>
    /// The run has been approved and is ready to execute.
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
    /// The run was canceled before completion.
    /// </summary>
    Canceled = 8,

    /// <summary>
    /// The run exceeded its timeout limit.
    /// </summary>
    TimedOut = 9
}
