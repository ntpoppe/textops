namespace TextOps.Contracts.Runs;

public enum RunStatus
{
    Created = 0,
    AwaitingApproval = 1,
    Approved = 2,
    Dispatching = 3,
    Running = 4,
    Succeeded = 5,
    Failed = 6,
    Denied = 7,
    Canceled = 8,
    TimedOut = 9
}
