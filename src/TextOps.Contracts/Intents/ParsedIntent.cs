namespace TextOps.Contracts.Intents;

/// <summary>
/// Represents a parsed user intent with extracted parameters.
/// </summary>
/// <remarks>
/// This is the result of parsing user input into a structured intent with
/// extracted parameters like job keys and run IDs.
/// </remarks>
/// <param name="Type">
/// The normalized intent. The parser turns free text
/// into one of the IntentType values.
/// </param>
/// <param name="RawText">
/// Original text from the user. Used for audit, debugging, and AI fallback later.
/// </param>
/// <param name="JobKey">
/// Which job the user referenced (if any). Example: "run nightly-backup" would
/// extract "nightly-backup" as the JobKey.
/// </param>
/// <param name="RunId">
/// Which run the user referenced (if any). Examples: "yes 8f3a" or "status 8f3a"
/// would extract "8f3a" as the RunId.
/// </param>
public sealed record ParsedIntent(
    IntentType Type,
    string RawText,
    string? JobKey,
    string? RunId
);
