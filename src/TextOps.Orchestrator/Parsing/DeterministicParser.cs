using System.Text.RegularExpressions;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Parsing;

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
    // Matches: "run myjob-123", "run backup_task", "run testjob"
    private static readonly Regex RunRegex =
        new(@"^run\s+(?<job>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches: "run", "run   " (run command with no job specified)
    private static readonly Regex RunWithoutJobRegex =
        new(@"^run\s*$", RegexOptions.IgnoreCase);

    // Matches: "yes 91ff4", "approve job-run-8", "approve aBc_123"
    private static readonly Regex ApproveRegex =
        new(@"^(yes|approve)\s+(?<run>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches: "no 9-a", "deny 123xyz", "deny my-run_5"
    private static readonly Regex DenyRegex =
        new(@"^(no|deny)\s+(?<run>[a-zA-Z0-9\-_]+)$", RegexOptions.IgnoreCase);

    // Matches: "status 23", "status job-abc_X", "status RUN-23"
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

        if (TryParseRunJob(input, out var runJobIntent))
            return runJobIntent;

        if (TryParseApprove(input, out var approveIntent))
            return approveIntent;

        if (TryParseDeny(input, out var denyIntent))
            return denyIntent;

        if (TryParseStatus(input, out var statusIntent))
            return statusIntent;

        return new ParsedIntent(IntentType.Unknown, input, JobKey: null, RunId: null);
    }

    private bool TryParseRunJob(string input, out ParsedIntent intent)
    {
        if (RunRegex.Match(input) is { Success: true } match)
        {
            intent = new ParsedIntent(
                IntentType.RunJob,
                input,
                JobKey: match.Groups["job"].Value,
                RunId: null
            );
            return true;
        }

        if (RunWithoutJobRegex.Match(input) is { Success: true })
        {
            intent = new ParsedIntent(
                IntentType.RunJob,
                input,
                JobKey: null,
                RunId: null
            );
            return true;
        }

        intent = default!;
        return false;
    }

    private bool TryParseApprove(string input, out ParsedIntent intent)
    {
        if (ApproveRegex.Match(input) is { Success: true } match)
        {
            intent = new ParsedIntent(
                IntentType.ApproveRun,
                input,
                JobKey: null,
                RunId: match.Groups["run"].Value
            );
            return true;
        }

        intent = default!;
        return false;
    }

    private bool TryParseDeny(string input, out ParsedIntent intent)
    {
        if (DenyRegex.Match(input) is { Success: true } match)
        {
            intent = new ParsedIntent(
                IntentType.DenyRun,
                input,
                JobKey: null,
                RunId: match.Groups["run"].Value
            );
            return true;
        }

        intent = default!;
        return false;
    }

    private bool TryParseStatus(string input, out ParsedIntent intent)
    {
        if (StatusRegex.Match(input) is { Success: true } match)
        {
            intent = new ParsedIntent(
                IntentType.Status,
                input,
                JobKey: null,
                RunId: match.Groups["run"].Value
            );
            return true;
        }

        intent = default!;
        return false;
    }
}
