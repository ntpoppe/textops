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
    public async Task<IActionResult> HandleInbound([FromBody] DevInboundRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var providerMessageId = EnsureProviderMessageId(request.ProviderMessageId);
        var inboundMessage = BuildInboundMessage(request, providerMessageId);
        var (parsedIntent, orchestratorResult) = await ProcessInboundMessageAsync(inboundMessage);
        await EnqueueDispatchIfPresentAsync(orchestratorResult);
        var response = MapToResponse(parsedIntent, orchestratorResult);

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

    private async Task<(ParsedIntent Intent, OrchestratorResult Result)> ProcessInboundMessageAsync(InboundMessage inboundMessage)
    {
        var parsedIntent = _parser.Parse(inboundMessage.Body);
        var orchestratorResult = await _orchestrator.HandleInboundAsync(inboundMessage, parsedIntent);
        return (parsedIntent, orchestratorResult);
    }

    private async Task EnqueueDispatchIfPresentAsync(OrchestratorResult orchestratorResult)
    {
        if (orchestratorResult.Dispatch != null)
        {
            await _executionDispatcher.EnqueueAsync(orchestratorResult.Dispatch);
        }
    }

    private static DevInboundResponse MapToResponse(ParsedIntent parsedIntent, OrchestratorResult orchestratorResult)
    {
        return new DevInboundResponse
        {
            IntentType = parsedIntent.Type.ToString(),
            JobKey = parsedIntent.JobKey,
            RunId = orchestratorResult.RunId,
            DispatchedExecution = orchestratorResult.DispatchedExecution,
            Outbound = orchestratorResult.Outbound.Select(outboundMessage => new OutboundMessageDto
            {
                Body = outboundMessage.Body,
                CorrelationId = outboundMessage.CorrelationId,
                IdempotencyKey = outboundMessage.IdempotencyKey,
                ChannelId = outboundMessage.ChannelId,
                Conversation = outboundMessage.Conversation.Value
            }).ToList()
        };
    }
}

