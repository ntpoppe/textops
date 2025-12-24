using TextOps.Contracts.Intents;

namespace TextOps.Contracts.Parsing;

/// <summary>
/// Parses raw text into a structured intent.
/// </summary>
public interface IIntentParser
{
    /// <summary>
    /// Parses the given text and returns a parsed intent.
    /// </summary>
    ParsedIntent Parse(string text);
}

