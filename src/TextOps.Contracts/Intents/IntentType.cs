namespace TextOps.Contracts.Intents;

/// <summary>
/// Represents what the user wants.
/// </summary>
/// <remarks>
/// The normalized intent extracted from user input.
/// The parser turns free text into one of these types.
/// </remarks>
public enum IntentType
{
    /// <summary>
    /// The intent could not be determined or is unrecognized.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// User wants to run a job.
    /// </summary>
    RunJob = 1,

    /// <summary>
    /// User wants to approve a pending run.
    /// </summary>
    ApproveRun = 2,

    /// <summary>
    /// User wants to deny a pending run.
    /// </summary>
    DenyRun = 3,

    /// <summary>
    /// User wants to check the status of a run or job.
    /// </summary>
    Status = 4
}
