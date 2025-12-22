using TextOps.Contracts.Intents;

namespace TextOps.Orchestrator.Parsing;

public interface IIntentParser
{
    ParsedIntent Parse(string text);
}
