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

    public OrchestratorResult HandleInbound(InboundMessage inboundMessage, ParsedIntent intent)
    {
        return HandleInboundAsync(inboundMessage, intent).GetAwaiter().GetResult();
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

    private async Task<OrchestratorResult> HandleInboundAsync(InboundMessage inboundMessage, ParsedIntent intent)
    {
        if (await IsInboundDuplicate(inboundMessage))
        {
            return CreateDuplicateInboundResult();
        }

        return await RouteByIntent(inboundMessage, intent);
    }

    private async Task<bool> IsInboundDuplicate(InboundMessage inboundMessage)
    {
        return await _repository.IsInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId);
    }

    private static OrchestratorResult CreateDuplicateInboundResult()
    {
        return new OrchestratorResult(
            RunId: null,
            Outbound: Array.Empty<OutboundMessage>(),
            DispatchedExecution: false,
            Dispatch: null
        );
    }

    private async Task<OrchestratorResult> RouteByIntent(InboundMessage inboundMessage, ParsedIntent intent)
    {
        return intent.Type switch
        {
            IntentType.RunJob => await HandleRunJobAsync(inboundMessage, intent),
            IntentType.ApproveRun => await HandleApproveAsync(inboundMessage, intent),
            IntentType.DenyRun => await HandleDenyAsync(inboundMessage, intent),
            IntentType.Status => await HandleStatusAsync(inboundMessage, intent),
            _ => await HandleUnknownAsync(inboundMessage)
        };
    }

    private async Task<OrchestratorResult> HandleRunJobAsync(InboundMessage inboundMessage, ParsedIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.JobKey))
        {
            await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, null);
            return CreateReplyMessage(inboundMessage, runId: null, "Missing job key. Usage: run <jobKey>");
        }

        var createdAt = DateTimeOffset.UtcNow;
        var runId = GenerateRunId();
        var run = CreateRun(runId, intent.JobKey!, inboundMessage, createdAt);
        var runEvents = CreateRunCreationEvents(runId, inboundMessage, createdAt, run.JobKey);

        await _repository.CreateRunAsync(run, runEvents);
        await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, runId);

        var outboundMessage = CreateApprovalRequestMessage(inboundMessage, runId, run.JobKey);
        return new OrchestratorResult(runId, new[] { outboundMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static Run CreateRun(string runId, string jobKey, InboundMessage inboundMessage, DateTimeOffset createdAt)
    {
        return new Run(
            RunId: runId,
            JobKey: jobKey,
            Status: RunStatus.AwaitingApproval,
            CreatedAt: createdAt,
            RequestedByAddress: inboundMessage.From.Value,
            ChannelId: inboundMessage.ChannelId,
            ConversationId: inboundMessage.Conversation.Value
        );
    }

    private static RunEvent[] CreateRunCreationEvents(string runId, InboundMessage inboundMessage, DateTimeOffset createdAt, string jobKey)
    {
        return new[]
        {
            new RunEvent(runId, "RunCreated", createdAt, GetActorFromMessage(inboundMessage), new { JobKey = jobKey }),
            new RunEvent(runId, "ApprovalRequested", createdAt, "system", new { Policy = "DefaultRequireApproval" })
        };
    }

    private static OutboundMessage CreateApprovalRequestMessage(InboundMessage inboundMessage, string runId, string jobKey)
    {
        return new OutboundMessage(
            ChannelId: inboundMessage.ChannelId,
            Conversation: inboundMessage.Conversation,
            To: null,
            Body: $"Job \"{jobKey}\" is ready. Reply YES {runId} to approve or NO {runId} to deny.",
            CorrelationId: runId,
            IdempotencyKey: $"approval-request:{runId}"
        );
    }

    private async Task<OrchestratorResult> HandleApproveAsync(InboundMessage inboundMessage, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return CreateReplyMessage(inboundMessage, runId: null, "Missing run id. Usage: yes <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return CreateReplyMessage(inboundMessage, runId: null, $"Unknown run id: {runId}");

        var createdAt = DateTimeOffset.UtcNow;
        var approvalEvents = CreateApprovalEvents(runId, inboundMessage, createdAt);

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.AwaitingApproval, RunStatus.Dispatching, approvalEvents);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);
            return CreateReplyMessage(inboundMessage, runId: run.RunId, $"Cannot approve run {run.RunId} in state {currentStatus}.");
        }

        var outboundMessage = CreateApprovalMessage(updatedRun);
        var executionDispatch = new ExecutionDispatch(updatedRun.RunId, updatedRun.JobKey);
        return new OrchestratorResult(updatedRun.RunId, new[] { outboundMessage }, DispatchedExecution: true, Dispatch: executionDispatch);
    }

    private static RunEvent[] CreateApprovalEvents(string runId, InboundMessage inboundMessage, DateTimeOffset createdAt)
    {
        return new[]
        {
            new RunEvent(runId, "RunApproved", createdAt, GetActorFromMessage(inboundMessage), new { }),
            new RunEvent(runId, "ExecutionDispatched", createdAt, "system", new { })
        };
    }

    private static OutboundMessage CreateApprovalMessage(Run updatedRun)
    {
        return new OutboundMessage(
            ChannelId: updatedRun.ChannelId,
            Conversation: new ConversationId(updatedRun.ConversationId),
            To: null,
            Body: $"Approved. Starting run {updatedRun.RunId} for job \"{updatedRun.JobKey}\"…",
            CorrelationId: updatedRun.RunId,
            IdempotencyKey: $"approved-starting:{updatedRun.RunId}"
        );
    }

    private async Task<OrchestratorResult> HandleDenyAsync(InboundMessage inboundMessage, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return CreateReplyMessage(inboundMessage, runId: null, "Missing run id. Usage: no <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return CreateReplyMessage(inboundMessage, runId: null, $"Unknown run id: {runId}");

        var createdAt = DateTimeOffset.UtcNow;
        var denialEvents = CreateDenialEvents(runId, inboundMessage, createdAt);

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.AwaitingApproval, RunStatus.Denied, denialEvents);

        if (updatedRun == null)
        {
            var currentStatus = await _repository.GetRunStatusAsync(runId);
            return CreateReplyMessage(inboundMessage, runId: run.RunId, $"Cannot deny run {run.RunId} in state {currentStatus}.");
        }

        var denialMessage = CreateDenialMessage(updatedRun);
        return CreateReplyMessage(inboundMessage, runId: updatedRun.RunId, denialMessage);
    }

    private static RunEvent[] CreateDenialEvents(string runId, InboundMessage inboundMessage, DateTimeOffset createdAt)
    {
        return new[]
        {
            new RunEvent(runId, "RunDenied", createdAt, GetActorFromMessage(inboundMessage), new { })
        };
    }

    private static string CreateDenialMessage(Run updatedRun)
    {
        return $"Denied run {updatedRun.RunId} for job \"{updatedRun.JobKey}\".";
    }

    private async Task<OrchestratorResult> HandleStatusAsync(InboundMessage inboundMessage, ParsedIntent intent)
    {
        await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, null);

        if (string.IsNullOrWhiteSpace(intent.RunId))
            return CreateReplyMessage(inboundMessage, runId: null, "Missing run id. Usage: status <runId>");

        var runId = intent.RunId!.Trim();
        var run = await _repository.GetRunAsync(runId);

        if (run == null)
            return CreateReplyMessage(inboundMessage, runId: null, $"Unknown run id: {runId}");

        var messageBody =
            $"Run {run.RunId}\n" +
            $"Job: {run.JobKey}\n" +
            $"State: {run.Status}\n" +
            $"Created: {run.CreatedAt:O}";

        return CreateReplyMessage(inboundMessage, runId: run.RunId, messageBody);
    }

    private async Task<OrchestratorResult> HandleUnknownAsync(InboundMessage inboundMessage)
    {
        await _repository.MarkInboxProcessedAsync(inboundMessage.ChannelId, inboundMessage.ProviderMessageId, null);

        var messageBody =
            "Unknown command.\n" +
            "Try:\n" +
            "- run <jobKey>\n" +
            "- yes <runId>\n" +
            "- no <runId>\n" +
            "- status <runId>";

        return CreateReplyMessage(inboundMessage, runId: null, messageBody);
    }

    private async Task<OrchestratorResult> OnExecutionStartedAsync(string runId, string workerId)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run == null)
        {
            return CreateUnknownRunStartError(runId);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var executionStartedEvents = CreateExecutionStartedEvent(runId, workerId, createdAt);

        var updatedRun = await _repository.TryUpdateRunAsync(runId, RunStatus.Dispatching, RunStatus.Running, executionStartedEvents);

        if (updatedRun == null)
        {
            return await HandleExecutionStartedError(runId, run);
        }

        return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
    }

    private static OrchestratorResult CreateUnknownRunStartError(string runId)
    {
        var errorMessage = new OutboundMessage(
            ChannelId: "system",
            Conversation: new ConversationId("system"),
            To: null,
            Body: $"Error: Cannot start execution for unknown run {runId}.",
            CorrelationId: runId,
            IdempotencyKey: $"execution-started-error:{runId}"
        );
        return new OrchestratorResult(runId, new[] { errorMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static RunEvent[] CreateExecutionStartedEvent(string runId, string workerId, DateTimeOffset createdAt)
    {
        return new[]
        {
            new RunEvent(runId, "ExecutionStarted", createdAt, $"worker:{workerId}", new { WorkerId = workerId })
        };
    }

    private async Task<OrchestratorResult> HandleExecutionStartedError(string runId, Run run)
    {
        var currentStatus = await _repository.GetRunStatusAsync(runId);

        if (currentStatus == RunStatus.Running)
        {
            return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
        }

        var errorMessage = new OutboundMessage(
            ChannelId: run.ChannelId,
            Conversation: new ConversationId(run.ConversationId),
            To: null,
            Body: $"Cannot start run {runId} in state {currentStatus}.",
            CorrelationId: runId,
            IdempotencyKey: $"execution-started-error-state:{runId}"
        );
        return new OrchestratorResult(runId, new[] { errorMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private async Task<OrchestratorResult> OnExecutionCompletedAsync(string runId, string workerId, bool success, string summary)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run == null)
        {
            return CreateUnknownRunCompletionError(runId);
        }

        var targetStatus = DetermineCompletionStatus(success);
        var completionEvents = CreateCompletionEvent(runId, workerId, success, summary);

        var updatedRun = await _repository.TryUpdateRunFromMultipleAsync(
            runId,
            new[] { RunStatus.Running, RunStatus.Dispatching },
            targetStatus,
            completionEvents);

        if (updatedRun == null)
        {
            return await HandleCompletionError(runId, run);
        }

        var completionMessage = CreateCompletionMessage(runId, run, success, summary);
        return new OrchestratorResult(runId, new[] { completionMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static OrchestratorResult CreateUnknownRunCompletionError(string runId)
    {
        var errorMessage = new OutboundMessage(
            ChannelId: "system",
            Conversation: new ConversationId("system"),
            To: null,
            Body: $"Error: Cannot complete execution for unknown run {runId}.",
            CorrelationId: runId,
            IdempotencyKey: $"execution-completed-error:{runId}"
        );
        return new OrchestratorResult(runId, new[] { errorMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static RunStatus DetermineCompletionStatus(bool success)
    {
        return success ? RunStatus.Succeeded : RunStatus.Failed;
    }

    private static RunEvent[] CreateCompletionEvent(string runId, string workerId, bool success, string summary)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var eventType = success ? "ExecutionSucceeded" : "ExecutionFailed";
        return new[]
        {
            new RunEvent(runId, eventType, createdAt, $"worker:{workerId}", new { WorkerId = workerId, Summary = summary })
        };
    }

    private async Task<OrchestratorResult> HandleCompletionError(string runId, Run run)
    {
        var currentStatus = await _repository.GetRunStatusAsync(runId);

        if (IsTerminalState(currentStatus))
        {
            return new OrchestratorResult(runId, Array.Empty<OutboundMessage>(), DispatchedExecution: false, Dispatch: null);
        }

        var errorMessage = new OutboundMessage(
            ChannelId: run.ChannelId,
            Conversation: new ConversationId(run.ConversationId),
            To: null,
            Body: $"Cannot complete run {runId} in state {currentStatus}.",
            CorrelationId: runId,
            IdempotencyKey: $"execution-completed-error-state:{runId}"
        );
        return new OrchestratorResult(runId, new[] { errorMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static bool IsTerminalState(RunStatus? status)
    {
        return status == RunStatus.Succeeded || status == RunStatus.Failed;
    }

    private static OutboundMessage CreateCompletionMessage(string runId, Run run, bool success, string summary)
    {
        var messageBody = success
            ? $"Run {runId} succeeded: {summary}"
            : $"Run {runId} failed: {summary}";

        return new OutboundMessage(
            ChannelId: run.ChannelId,
            Conversation: new ConversationId(run.ConversationId),
            To: null,
            Body: messageBody,
            CorrelationId: runId,
            IdempotencyKey: $"execution-completed:{runId}"
        );
    }

    private static OrchestratorResult CreateReplyMessage(InboundMessage inboundMessage, string? runId, string messageBody)
    {
        var outboundMessage = new OutboundMessage(
            ChannelId: inboundMessage.ChannelId,
            Conversation: inboundMessage.Conversation,
            To: null,
            Body: messageBody,
            CorrelationId: runId ?? "none",
            IdempotencyKey: $"reply:{inboundMessage.ChannelId}:{inboundMessage.ProviderMessageId}"
        );

        return new OrchestratorResult(runId, new[] { outboundMessage }, DispatchedExecution: false, Dispatch: null);
    }

    private static string GetActorFromMessage(InboundMessage inboundMessage) => $"user:{inboundMessage.From.Value}";

    private static string GenerateRunId()
    {
        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }
}

