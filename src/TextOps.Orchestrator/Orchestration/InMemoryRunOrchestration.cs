using System.Collections.Concurrent;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Orchestration;

/// <summary>
/// In-memory implementation of <see cref="IRunOrchestrator"/>.
/// </summary>
/// <remarks>
/// <para>Responsibilities:</para>
/// <list type="bullet">
/// <item><description>Enforces approval gating and state transitions</description></item>
/// <item><description>Maintains append-only RunEvent timeline</description></item>
/// <item><description>Idempotent inbound handling via (ChannelId, ProviderMessageId)</description></item>
/// <item><description>Produces OutboundMessage effects (does not send) and dispatch signals</description></item>
/// </list>
/// <para>Future: Replace with a persistent implementation backed by a database.</para>
/// </remarks>
public sealed class InMemoryRunOrchestrator : IRunOrchestrator
{
    // Inbox idempotency: key = "<channelId>:<providerMessageId>"
    private readonly ConcurrentDictionary<string, byte> _inbox = new();

    // Run snapshots
    private readonly ConcurrentDictionary<string, Run> _runs = new();

    // Append-only events per run
    private readonly ConcurrentDictionary<string, List<RunEvent>> _events = new();

    public OrchestratorResult HandleInbound(InboundMessage msg, ParsedIntent intent)
    {
        // Idempotency guard
        var inboxKey = InboxKey(msg.ChannelId, msg.ProviderMessageId);
        if (!_inbox.TryAdd(inboxKey, 0))
        {
            // duplicate delivery -> no effects
            return new OrchestratorResult(
                RunId: null,
                Outbound: Array.Empty<OutboundMessage>(),
                DispatchedExecution: false,
                Dispatch: null
            );
        }

        // Route by intent
        return intent.Type switch
        {
            IntentType.RunJob => HandleRunJob(msg, intent),
            IntentType.ApproveRun => HandleApprove(msg, intent),
            IntentType.DenyRun => HandleDeny(msg, intent),
            IntentType.Status => HandleStatus(msg, intent),
            _ => HandleUnknown(msg)
        };
    }

    public RunTimeline GetTimeline(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
            throw new KeyNotFoundException($"Run not found: {runId}");

        var list = _events.TryGetValue(runId, out var evts) ? evts : new List<RunEvent>();

        // List<T> isn't thread-safe; return a copy
        RunEvent[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }

        return new RunTimeline(run, snapshot);
    }

    // ------------------------
    // Intent Handlers
    // ------------------------

    private OrchestratorResult HandleRunJob(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.JobKey))
            return Reply(msg, runId: null, "Missing job key. Usage: run <jobKey>");

        var now = DateTimeOffset.UtcNow;
        var runId = NewRunId();

        var run = new Run(
            RunId: runId,
            JobKey: intent.JobKey!,
            Status: RunStatus.AwaitingApproval,
            CreatedAt: now,
            RequestedByAddress: msg.From.Value,
            ChannelId: msg.ChannelId,
            ConversationId: msg.Conversation.Value
        );

        _runs[runId] = run;

        Append(runId, "RunCreated", now, ActorFrom(msg), new { run.JobKey });
        Append(runId, "ApprovalRequested", now, "system", new { Policy = "DefaultRequireApproval" });

        var outbound = new OutboundMessage(
            ChannelId: msg.ChannelId,
            Conversation: msg.Conversation,
            To: null,
            Body: $"Job \"{run.JobKey}\" is ready. Reply YES {runId} to approve or NO {runId} to deny.",
            CorrelationId: runId,
            IdempotencyKey: $"approval-request:{runId}"
        );

        return new OrchestratorResult(runId, new[] { outbound }, DispatchedExecution: false, Dispatch: null);
    }

    private OrchestratorResult HandleApprove(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: yes <runId>");

        var runId = intent.RunId!.Trim();

        if (!_runs.TryGetValue(runId, out var run))
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        // Atomic state transition: AwaitingApproval -> Dispatching
        // This prevents race conditions where two concurrent approvals both succeed
        var newRun = run with { Status = RunStatus.Dispatching };
        if (!TryTransition(runId, run, newRun, RunStatus.AwaitingApproval, out var currentStatus))
        {
            return Reply(msg, runId: run.RunId, $"Cannot approve run {run.RunId} in state {currentStatus}.");
        }

        var now = DateTimeOffset.UtcNow;
        Append(newRun.RunId, "RunApproved", now, ActorFrom(msg), new { });
        Append(newRun.RunId, "ExecutionDispatched", now, "system", new { });

        var outbound = new OutboundMessage(
            ChannelId: newRun.ChannelId,
            Conversation: new ConversationId(newRun.ConversationId),
            To: null,
            Body: $"Approved. Starting run {newRun.RunId} for job \"{newRun.JobKey}\"…",
            CorrelationId: newRun.RunId,
            IdempotencyKey: $"approved-starting:{newRun.RunId}"
        );

        var dispatch = new ExecutionDispatch(newRun.RunId, newRun.JobKey);
        return new OrchestratorResult(newRun.RunId, new[] { outbound }, DispatchedExecution: true, Dispatch: dispatch);
    }

    private OrchestratorResult HandleDeny(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: no <runId>");

        var runId = intent.RunId!.Trim();

        if (!_runs.TryGetValue(runId, out var run))
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        // Atomic state transition: AwaitingApproval -> Denied
        var newRun = run with { Status = RunStatus.Denied };
        if (!TryTransition(runId, run, newRun, RunStatus.AwaitingApproval, out var currentStatus))
        {
            return Reply(msg, runId: run.RunId, $"Cannot deny run {run.RunId} in state {currentStatus}.");
        }

        var now = DateTimeOffset.UtcNow;
        Append(newRun.RunId, "RunDenied", now, ActorFrom(msg), new { });

        return Reply(msg, runId: newRun.RunId, $"Denied run {newRun.RunId} for job \"{newRun.JobKey}\".");
    }

    private OrchestratorResult HandleStatus(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: status <runId>");

        var runId = intent.RunId!.Trim();

        if (!_runs.TryGetValue(runId, out var run))
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        var body =
            $"Run {run.RunId}\n" +
            $"Job: {run.JobKey}\n" +
            $"State: {run.Status}\n" +
            $"Created: {run.CreatedAt:O}";

        return Reply(msg, runId: run.RunId, body);
    }

    private OrchestratorResult HandleUnknown(InboundMessage msg)
    {
        var body =
            "Unknown command.\n" +
            "Try:\n" +
            "- run <jobKey>\n" +
            "- yes <runId>\n" +
            "- no <runId>\n" +
            "- status <runId>";

        return Reply(msg, runId: null, body);
    }

    // ------------------------
    // Execution Callbacks
    // ------------------------

    public OrchestratorResult OnExecutionStarted(string runId, string workerId)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            // Run not found - return error message
            var errorOutbound = new OutboundMessage(
                ChannelId: "system",
                Conversation: new ConversationId("system"),
                To: null,
                Body: $"Error: Cannot start execution for unknown run {runId}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-started-error:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        // Atomic state transition: Dispatching -> Running
        var newRun = run with { Status = RunStatus.Running };
        if (!TryTransition(runId, run, newRun, RunStatus.Dispatching, out var currentStatus))
        {
            // Idempotency: if already Running, treat as no-op
            if (currentStatus == RunStatus.Running)
            {
                return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
            }

            // Invalid state - return error
            var errorOutbound = new OutboundMessage(
                ChannelId: run.ChannelId,
                Conversation: new ConversationId(run.ConversationId),
                To: null,
                Body: $"Cannot start run {runId} in state {currentStatus}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-started-error-state:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        var now = DateTimeOffset.UtcNow;
        Append(newRun.RunId, "ExecutionStarted", now, $"worker:{workerId}", new { WorkerId = workerId });

        return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
    }

    public OrchestratorResult OnExecutionCompleted(string runId, string workerId, bool success, string summary)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            // Run not found - return error message
            var errorOutbound = new OutboundMessage(
                ChannelId: "system",
                Conversation: new ConversationId("system"),
                To: null,
                Body: $"Error: Cannot complete execution for unknown run {runId}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-completed-error:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        var newStatus = success ? RunStatus.Succeeded : RunStatus.Failed;
        var newRun = run with { Status = newStatus };

        // Atomic state transition: Running or Dispatching -> Terminal
        // Try from Running first, then from Dispatching
        if (!TryTransitionFromMultiple(runId, run, newRun, new[] { RunStatus.Running, RunStatus.Dispatching }, out var currentStatus))
        {
            // Idempotency: if already in terminal state, treat as no-op
            if (currentStatus == RunStatus.Succeeded || currentStatus == RunStatus.Failed)
            {
                return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
            }

            // Invalid state - return error
            var errorOutbound = new OutboundMessage(
                ChannelId: run.ChannelId,
                Conversation: new ConversationId(run.ConversationId),
                To: null,
                Body: $"Cannot complete run {runId} in state {currentStatus}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-completed-error-state:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        var now = DateTimeOffset.UtcNow;
        var eventType = success ? "ExecutionSucceeded" : "ExecutionFailed";
        Append(newRun.RunId, eventType, now, $"worker:{workerId}", new { WorkerId = workerId, Summary = summary });

        // Emit completion message to original conversation
        var messageBody = success
            ? $"Run {runId} succeeded: {summary}"
            : $"Run {runId} failed: {summary}";

        var completionOutbound = new OutboundMessage(
            ChannelId: newRun.ChannelId,
            Conversation: new ConversationId(newRun.ConversationId),
            To: null,
            Body: messageBody,
            CorrelationId: runId,
            IdempotencyKey: $"execution-completed:{runId}"
        );

        return new OrchestratorResult(runId, new[] { completionOutbound }, DispatchedExecution: false, Dispatch: null);
    }

    // ------------------------
    // Atomic State Transitions
    // ------------------------

    /// <summary>
    /// Atomically transitions a run from expectedState to newRun.Status.
    /// Returns true if the transition succeeded, false otherwise.
    /// On failure, currentStatus contains the actual current status.
    /// </summary>
    private bool TryTransition(string runId, Run expectedRun, Run newRun, RunStatus expectedState, out RunStatus currentStatus)
    {
        // Verify expected state matches
        if (expectedRun.Status != expectedState)
        {
            currentStatus = expectedRun.Status;
            return false;
        }

        // Attempt atomic compare-and-swap
        // ConcurrentDictionary.TryUpdate compares by value for records
        if (_runs.TryUpdate(runId, newRun, expectedRun))
        {
            currentStatus = newRun.Status;
            return true;
        }

        // Swap failed - another thread modified the run
        // Re-read to get current status
        if (_runs.TryGetValue(runId, out var current))
        {
            currentStatus = current.Status;
        }
        else
        {
            // Run was deleted (shouldn't happen in normal operation)
            currentStatus = expectedRun.Status;
        }
        return false;
    }

    /// <summary>
    /// Atomically transitions a run from any of the expected states to newRun.Status.
    /// Returns true if the transition succeeded, false otherwise.
    /// </summary>
    private bool TryTransitionFromMultiple(string runId, Run expectedRun, Run newRun, RunStatus[] expectedStates, out RunStatus currentStatus)
    {
        // Check if current state is one of the expected states
        if (!expectedStates.Contains(expectedRun.Status))
        {
            currentStatus = expectedRun.Status;
            return false;
        }

        // Attempt atomic compare-and-swap
        if (_runs.TryUpdate(runId, newRun, expectedRun))
        {
            currentStatus = newRun.Status;
            return true;
        }

        // Swap failed - re-read to get current status
        if (_runs.TryGetValue(runId, out var current))
        {
            currentStatus = current.Status;
        }
        else
        {
            currentStatus = expectedRun.Status;
        }
        return false;
    }

    // ------------------------
    // Helpers
    // ------------------------

    private OrchestratorResult Reply(InboundMessage msg, string? runId, string body)
    {
        var outbound = new OutboundMessage(
            ChannelId: msg.ChannelId,
            Conversation: msg.Conversation,
            To: null,
            Body: body,
            CorrelationId: runId ?? "none",
            IdempotencyKey: $"reply:{InboxKey(msg.ChannelId, msg.ProviderMessageId)}"
        );

        return new OrchestratorResult(runId, new[] { outbound }, DispatchedExecution: false, Dispatch: null);
    }

    private void Append(string runId, string type, DateTimeOffset at, string actor, object payload)
    {
        var list = _events.GetOrAdd(runId, _ => new List<RunEvent>());
        lock (list)
        {
            list.Add(new RunEvent(runId, type, at, actor, payload));
        }
    }

    private static string ActorFrom(InboundMessage msg) => $"user:{msg.From.Value}";

    private static string InboxKey(string channelId, string providerMessageId)
        => $"{channelId}:{providerMessageId}";

    private static string NewRunId()
    {
        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }
}
