namespace TextOps.Contracts.Intents;

public sealed record ParsedIntent(
    IntentType Type,
    string RawText,
    string? JobKey,
    string? RunId
);
