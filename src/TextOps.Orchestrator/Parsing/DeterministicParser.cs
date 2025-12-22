using System.Text.RegularExpressions;
using TextOps.Contracts.Intents;

namespace TextOps.Orchestrator.Parsing;

/// <summary>
/// Deterministic, rules-only intent parser.
///
/// <para>
/// This parser converts raw user text into a structured <see cref="ParsedIntent"/> using a strict grammar.
/// It is intentionally conservative: if input does not match the supported patterns exactly, it returns
/// <see cref="IntentType.Unknown"/> rather than guessing.
/// </para>
///
/// <para>
/// Supported command grammar:
/// </para>
/// <list type="bullet">
/// <item><description><c>run &lt;jobKey&gt;</c> — Start a job (jobKey: letters/digits/'-'/ '_').</description></item>
/// <item><description><c>yes &lt;runId&gt;</c> or <c>approve &lt;runId&gt;</c> — Approve a run.</description></item>
/// <item><description><c>no &lt;runId&gt;</c> or <c>deny &lt;runId&gt;</c> — Deny a run.</description></item>
/// <item><description><c>status &lt;runId&gt;</c> — Get the current status of a run.</description></item>
/// </list>
/// </summary>
public sealed class DeterministicIntentParser : IIntentParser
{
    // Matches commands like: "run nightly-backup" => job: nightly-backup
    private static readonly Regex RunRegex =
        new(@"^run\s+(?<job>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches commands like: "yes 8f3a" or "approve 123-abc" => run: 8f3a or 123-abc
    private static readonly Regex ApproveRegex =
        new(@"^(yes|approve)\s+(?<run>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches commands like: "no 8f3a" or "deny 456-def" => run: 8f3a or 456-def
    private static readonly Regex DenyRegex =
        new(@"^(no|deny)\s+(?<run>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches commands like: "status 884422" or "status run-321" => run: 884422 or run-321
    private static readonly Regex StatusRegex =
        new(@"^status\s+(?<run>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);


    /// <summary>
    /// Parses a raw text command into a <see cref="ParsedIntent"/>.
    /// </summary>
    /// <param name="text">User-provided text to parse.</param>
    /// <returns>
    /// A <see cref="ParsedIntent"/> with a normalized <see cref="ParsedIntent.Type"/> and extracted identifiers,
    /// or <see cref="IntentType.Unknown"/> if the input does not match the supported grammar exactly.
    /// </returns>
    public ParsedIntent Parse(string text)
    {
        var input = text.Trim();

        if (RunRegex.Match(input) is { Success: true } run)
        {
            return new ParsedIntent(
                IntentType.RunJob,
                input,
                JobKey: run.Groups["job"].Value,
                RunId: null
            );
        }

        if (ApproveRegex.Match(input) is { Success: true } approve)
        {
            return new ParsedIntent(
                IntentType.ApproveRun,
                input,
                JobKey: null,
                RunId: approve.Groups["run"].Value
            );
        }

        if (DenyRegex.Match(input) is { Success: true } deny)
        {
            return new ParsedIntent(
                IntentType.DenyRun,
                input,
                JobKey: null,
                RunId: deny.Groups["run"].Value
            );
        }

        if (StatusRegex.Match(input) is { Success: true } status)
        {
            return new ParsedIntent(
                IntentType.Status,
                input,
                JobKey: null,
                RunId: status.Groups["run"].Value
            );
        }

        return new ParsedIntent(
            IntentType.Unknown,
            input,
            JobKey: null,
            RunId: null
        );
    }
}
