using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Runs;
using TextOps.Persistence.Repositories;

namespace TextOps.Orchestrator.Orchestration;

/// <summary>
/// Persistent implementation of <see cref="IRunOrchestrator"/> backed by a database.
/// </summary>
/// <remarks>
/// <para>Responsibilities:</para>
/// <list type="bullet">
/// <item><description>Enforces approval gating and state transitions</description></item>
/// <item><description>Maintains append-only RunEvent timeline (persisted)</description></item>
/// <item><description>Idempotent inbound handling via database deduplication</description></item>
/// <item><description>Produces OutboundMessage effects (does not send) and dispatch signals</description></item>
/// </list>
/// </remarks>
public sealed class PersistentRunOrchestrator : IRunOrchestrator
{
    private readonly IRunRepository _repository;

    public PersistentRunOrchestrator(IRunRepository repository)
    {
        _repository = repository;
    }

    public OrchestratorResult HandleInbound(InboundMessage msg, ParsedIntent intent)
    {
        // Use synchronous wrapper for the interface (async operations internally)
        return HandleInboundAsync(msg, intent).GetAwaiter().GetResult();
    }

    public RunTimeline GetTimeline(string runId)
    {
        var timeline = _repository.GetTimelineAsync(runId).GetAwaiter().GetResult();
        if (timeline == null)
            throw new KeyNotFoundException($"Run not found: {runId}");
        return timeline;
    }

    public OrchestratorResult OnExecutionStarted(string runId, string workerId)
    {
        return OnExecutionStartedAsync(runId, workerId).GetAwaiter().GetResult();
    }

    public OrchestratorResult OnExecutionCompleted(string runId, string workerId, bool success, string summary)
    {
        return OnExecutionCompletedAsync(runId, workerId, success, summary).GetAwaiter().GetResult();
    }

    // ------------------------
    // Async Implementation
    // ------------------------

    private async Task<OrchestratorResult> HandleInboundAsync(InboundMessage msg, ParsedIntent intent)
    {
        // Idempotency guard
        if (await _repository.IsInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId))
        {
            // Duplicate delivery -> no effects
            return new OrchestratorResult(
                RunId: null,
                Outbound: Array.Empty<OutboundMessage>(),
                DispatchedExecution: false,
                Dispatch: null
            );
        }

        // Route by intent
        var result = intent.Type switch
        {
            IntentType.RunJob => await HandleRunJobAsync(msg, intent),
            IntentType.ApproveRun => await HandleApproveAsync(msg, intent),
            IntentType.DenyRun => await HandleDenyAsync(msg, intent),
            IntentType.Status => await HandleStatusAsync(msg, intent),
            _ => await HandleUnknownAsync(msg)
        };

        return result;
    }

    private async Task<OrchestratorResult> HandleRunJobAsync(InboundMessage msg, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.JobKey))
        {
            await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, null);
            return Reply(msg, runId: null, "Missing job key. Usage: run <jobKey>");
        }

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

        var events = new[]
        {
            new RunEvent(runId, "RunCreated", now, ActorFrom(msg), new { run.JobKey }),
            new RunEvent(runId, "ApprovalRequested", now, "system", new { Policy = "DefaultRequireApproval" })
        };

        await _repository.CreateRunAsync(run, events);
        await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, runId);

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

    private async Task<OrchestratorResult> HandleApproveAsync(InboundMessage msg, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: yes <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new RunEvent(runId, "RunApproved", now, ActorFrom(msg), new { }),
            new RunEvent(runId, "ExecutionDispatched", now, "system", new { })
        };

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.AwaitingApproval, RunStatus.Dispatching, events);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);
            return Reply(msg, runId: run.RunId, $"Cannot approve run {run.RunId} in state {currentStatus}.");
        }

        var outbound = new OutboundMessage(
            ChannelId: updatedRun.ChannelId,
            Conversation: new ConversationId(updatedRun.ConversationId),
            To: null,
            Body: $"Approved. Starting run {updatedRun.RunId} for job \"{updatedRun.JobKey}\"…",
            CorrelationId: updatedRun.RunId,
            IdempotencyKey: $"approved-starting:{updatedRun.RunId}"
        );

        var dispatch = new ExecutionDispatch(updatedRun.RunId, updatedRun.JobKey);
        return new OrchestratorResult(updatedRun.RunId, new[] { outbound }, DispatchedExecution: true, Dispatch: dispatch);
    }

    private async Task<OrchestratorResult> HandleDenyAsync(InboundMessage msg, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: no <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new RunEvent(runId, "RunDenied", now, ActorFrom(msg), new { })
        };

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.AwaitingApproval, RunStatus.Denied, events);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);
            return Reply(msg, runId: run.RunId, $"Cannot deny run {run.RunId} in state {currentStatus}.");
        }

        return Reply(msg, runId: updatedRun.RunId, $"Denied run {updatedRun.RunId} for job \"{updatedRun.JobKey}\".");
    }

    private async Task<OrchestratorResult> HandleStatusAsync(InboundMessage msg, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return Reply(msg, runId: null, "Missing run id. Usage: status <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return Reply(msg, runId: null, $"Unknown run id: {runId}");

        var body =
            $"Run {run.RunId}\n" +
            $"Job: {run.JobKey}\n" +
            $"State: {run.Status}\n" +
            $"Created: {run.CreatedAt:O}";

        return Reply(msg, runId: run.RunId, body);
    }

    private async Task<OrchestratorResult> HandleUnknownAsync(InboundMessage msg)
    {
        await _repository.MarkInboxProcessedAsync(msg.ChannelId, msg.ProviderMessageId, null);

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

    private async Task<OrchestratorResult> OnExecutionStartedAsync(string runId, string workerId)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run == null)
        {
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

        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new RunEvent(runId, "ExecutionStarted", now, $"worker:{workerId}", new { WorkerId = workerId })
        };

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.Dispatching, RunStatus.Running, events);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);

            // Idempotency: if already Running, treat as no-op
            if (currentStatus == RunStatus.Running)
                return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);

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

        return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
    }

    private async Task<OrchestratorResult> OnExecutionCompletedAsync(string runId, string workerId, bool success, string summary)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run == null)
        {
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
        var now = DateTimeOffset.UtcNow;
        var eventType = success ? "ExecutionSucceeded" : "ExecutionFailed";

        var events = new[]
        {
            new RunEvent(runId, eventType, now, $"worker:{workerId}", new { WorkerId = workerId, Summary = summary })
        };

        var updatedRun = await _repository.TryUpdateRunFromMultipleAsync(
            runId,
            new[] { RunStatus.Running, RunStatus.Dispatching },
            newStatus,
            events);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);

            // Idempotency: if already in terminal state, treat as no-op
            if (currentStatus == RunStatus.Succeeded || currentStatus == RunStatus.Failed)
                return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);

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

    private static OrchestratorResult Reply(InboundMessage msg, string? runId, string body)
    {
        var outbound = new OutboundMessage(
            ChannelId: msg.ChannelId,
            Conversation: msg.Conversation,
            To: null,
            Body: body,
            CorrelationId: runId ?? "none",
            IdempotencyKey: $"reply:{msg.ChannelId}:{msg.ProviderMessageId}"
        );

        return new OrchestratorResult(runId, new[] { outbound }, DispatchedExecution: false, Dispatch: null);
    }

    private static string ActorFrom(InboundMessage msg) => $"user:{msg.From.Value}";

    private static string NewRunId()
    {
        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }
}

