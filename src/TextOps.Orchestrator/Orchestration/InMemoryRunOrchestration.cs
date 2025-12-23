using System.Collections.Concurrent;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Runs;

namespace TextOps.Orchestrator.Orchestration;

/// <summary>
/// Step 4 MVP orchestrator:
/// - Enforces approval gating + simple state transitions
/// - Append-only RunEvent timeline
/// - Idempotent inbound handling via (ChannelId, ProviderMessageId)
/// - Produces OutboundMessage effects (does not send) and a dispatch signal (bool)
/// </summary>
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

        if (run.Status != RunStatus.AwaitingApproval)
            return Reply(msg, runId: run.RunId, $"Cannot approve run {run.RunId} in state {run.Status}.");

        var now = DateTimeOffset.UtcNow;

        // Transition: AwaitingApproval -> Dispatching
        run = run with { Status = RunStatus.Dispatching };
        _runs[run.RunId] = run;

        Append(run.RunId, "RunApproved", now, ActorFrom(msg), new { });
        Append(run.RunId, "ExecutionDispatched", now, "system", new { });

        var outbound = new OutboundMessage(
            ChannelId: run.ChannelId,
            Conversation: new ConversationId(run.ConversationId),
            To: null,
            Body: $"Approved. Starting run {run.RunId} for job \"{run.JobKey}\"…",
            CorrelationId: run.RunId,
            IdempotencyKey: $"approved-starting:{run.RunId}"
        );

        var dispatch = new ExecutionDispatch(run.RunId, run.JobKey);
        return new OrchestratorResult(run.RunId, new[] { outbound }, DispatchedExecution: true, Dispatch: dispatch);
    }

    private OrchestratorResult HandleDeny(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: no <runId>");

        var runId = intent.RunId!.Trim();

        if (!_runs.TryGetValue(runId, out var run))
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        if (run.Status != RunStatus.AwaitingApproval)
            return Reply(msg, runId: run.RunId, $"Cannot deny run {run.RunId} in state {run.Status}.");

        var now = DateTimeOffset.UtcNow;

        run = run with { Status = RunStatus.Denied };
        _runs[run.RunId] = run;

        Append(run.RunId, "RunDenied", now, ActorFrom(msg), new { });

        return Reply(msg, runId: run.RunId, $"Denied run {run.RunId} for job \"{run.JobKey}\".");
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

        // Idempotency: if already Running, treat as no-op
        if (run.Status == RunStatus.Running)
        {
            return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
        }

        // Only transition if in Dispatching state
        if (run.Status != RunStatus.Dispatching)
        {
            var errorOutbound = new OutboundMessage(
                ChannelId: run.ChannelId,
                Conversation: new ConversationId(run.ConversationId),
                To: null,
                Body: $"Cannot start run {runId} in state {run.Status}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-started-error-state:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        var now = DateTimeOffset.UtcNow;

        // Transition: Dispatching -> Running
        run = run with { Status = RunStatus.Running };
        _runs[run.RunId] = run;

        Append(run.RunId, "ExecutionStarted", now, $"worker:{workerId}", new { WorkerId = workerId });

        return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
    }

    public OrchestratorResult OnExecutionCompleted(string runId, bool success, string summary)
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

        // Idempotency: if already in terminal state, treat as no-op
        if (run.Status == RunStatus.Succeeded || run.Status == RunStatus.Failed)
        {
            return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
        }

        // Allow completion from Running or Dispatching (robustness: handle missed started event)
        if (run.Status != RunStatus.Running && run.Status != RunStatus.Dispatching)
        {
            var errorOutbound = new OutboundMessage(
                ChannelId: run.ChannelId,
                Conversation: new ConversationId(run.ConversationId),
                To: null,
                Body: $"Cannot complete run {runId} in state {run.Status}.",
                CorrelationId: runId,
                IdempotencyKey: $"execution-completed-error-state:{runId}"
            );
            return new OrchestratorResult(runId, new[] { errorOutbound }, DispatchedExecution: false, Dispatch: null);
        }

        var now = DateTimeOffset.UtcNow;
        var newStatus = success ? RunStatus.Succeeded : RunStatus.Failed;
        var eventType = success ? "ExecutionSucceeded" : "ExecutionFailed";

        // Transition to terminal state
        run = run with { Status = newStatus };
        _runs[run.RunId] = run;

        Append(run.RunId, eventType, now, "worker", new { Summary = summary });

        // Emit completion message to original conversation
        var messageBody = success
            ? $"Run {runId} succeeded: {summary}"
            : $"Run {runId} failed: {summary}";

        var completionOutbound = new OutboundMessage(
            ChannelId: run.ChannelId,
            Conversation: new ConversationId(run.ConversationId),
            To: null,
            Body: messageBody,
            CorrelationId: runId,
            IdempotencyKey: $"execution-completed:{runId}"
        );

        return new OrchestratorResult(runId, new[] { completionOutbound }, DispatchedExecution: false, Dispatch: null);
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
