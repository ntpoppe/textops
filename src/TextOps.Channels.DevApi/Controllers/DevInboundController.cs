using Microsoft.AspNetCore.Mvc;
using TextOps.Channels.DevApi.Dtos;
using TextOps.Channels.DevApi.Execution;
using TextOps.Contracts.Messaging;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

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

        // Generate provider message ID if not provided
        var providerMessageId = request.ProviderMessageId ?? Guid.NewGuid().ToString("n");

        // Prefix addresses/conversations with "dev:" if not already prefixed
        var conversationValue = request.Conversation.StartsWith("dev:", StringComparison.OrdinalIgnoreCase)
            ? request.Conversation
            : $"dev:{request.Conversation}";

        var fromValue = request.From.StartsWith("dev:", StringComparison.OrdinalIgnoreCase)
            ? request.From
            : $"dev:{request.From}";

        // Construct InboundMessage
        var inbound = new InboundMessage(
            ChannelId: ChannelIds.Dev,
            ProviderMessageId: providerMessageId,
            Conversation: new ConversationId(conversationValue),
            From: new Address(fromValue),
            To: null,
            Body: request.Body,
            ReceivedAt: DateTimeOffset.UtcNow,
            ProviderMeta: new Dictionary<string, string> { ["source"] = "devapi" }
        );

        // Parse intent and handle
        var intent = _parser.Parse(inbound.Body);
        var result = _orchestrator.HandleInbound(inbound, intent);

        // Enqueue execution dispatch if present
        if (result.Dispatch != null)
        {
            _executionDispatcher.Enqueue(result.Dispatch);
        }

        // Map to response DTO
        var response = new DevInboundResponse
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

        return Ok(response);
    }
}

