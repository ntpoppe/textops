namespace TextOps.Contracts.Runs;

public sealed record RunEvent(
    string RunId,
    string Type,
    DateTimeOffset At,
    string Actor,
    object Payload
);
