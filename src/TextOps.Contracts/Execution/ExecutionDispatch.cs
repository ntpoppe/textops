namespace TextOps.Contracts.Execution;

/// <summary>
/// Represents a request to execute a job run.
/// </summary>
/// <param name="RunId">The run identifier to execute.</param>
/// <param name="JobKey">The job key to execute.</param>
public sealed record ExecutionDispatch(string RunId, string JobKey);

