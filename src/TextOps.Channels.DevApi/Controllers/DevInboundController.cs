using Microsoft.AspNetCore.Mvc;
using TextOps.Channels.DevApi.Dtos;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Intents;
using TextOps.Contracts.Messaging;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;

namespace TextOps.Channels.DevApi.Controllers;

[ApiController]
[Route("dev")]
public sealed class DevInboundController : ControllerBase
{
    private readonly IIntentParser _parser;
    private readonly IRunOrchestrator _orchestrator;
    private readonly IExecutionDispatcher _executionDispatcher;

    public DevInboundController(
        IIntentParser parser,
        IRunOrchestrator orchestrator,
        IExecutionDispatcher executionDispatcher)
    {
        _parser = parser;
        _orchestrator = orchestrator;
        _executionDispatcher = executionDispatcher;
    }

    [HttpPost("inbound")]
    [ProducesResponseType(typeof(DevInboundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult HandleInbound([FromBody] DevInboundRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var providerMessageId = EnsureProviderMessageId(request.ProviderMessageId);
        var inbound = BuildInboundMessage(request, providerMessageId);
        var (intent, result) = ProcessInboundMessage(inbound);
        EnqueueDispatchIfPresent(result);
        var response = MapToResponse(intent, result);

        return Ok(response);
    }

    private static string EnsureProviderMessageId(string? providerMessageId)
    {
        return providerMessageId ?? Guid.NewGuid().ToString("n");
    }

    private static string EnsureDevPrefix(string value)
    {
        return value.StartsWith("dev:", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"dev:{value}";
    }

    private InboundMessage BuildInboundMessage(DevInboundRequest request, string providerMessageId)
    {
        var conversationValue = EnsureDevPrefix(request.Conversation);
        var fromValue = EnsureDevPrefix(request.From);

        return new InboundMessage(
            ChannelId: ChannelIds.Dev,
            ProviderMessageId: providerMessageId,
            Conversation: new ConversationId(conversationValue),
            From: new Address(fromValue),
            To: null,
            Body: request.Body,
            ReceivedAt: DateTimeOffset.UtcNow,
            ProviderMeta: new Dictionary<string, string> { ["source"] = "devapi" }
        );
    }

    private (ParsedIntent Intent, OrchestratorResult Result) ProcessInboundMessage(InboundMessage inbound)
    {
        var intent = _parser.Parse(inbound.Body);
        var result = _orchestrator.HandleInbound(inbound, intent);
        return (intent, result);
    }

    private void EnqueueDispatchIfPresent(OrchestratorResult result)
    {
        if (result.Dispatch != null)
        {
            _executionDispatcher.Enqueue(result.Dispatch);
        }
    }

    private static DevInboundResponse MapToResponse(ParsedIntent intent, OrchestratorResult result)
    {
        return new DevInboundResponse
        {
            IntentType = intent.Type.ToString(),
            JobKey = intent.JobKey,
            RunId = result.RunId,
            DispatchedExecution = result.DispatchedExecution,
            Outbound = result.Outbound.Select(o => new OutboundMessageDto
            {
                Body = o.Body,
                CorrelationId = o.CorrelationId,
                IdempotencyKey = o.IdempotencyKey,
                ChannelId = o.ChannelId,
                Conversation = o.Conversation.Value
            }).ToList()
        };
    }
}

