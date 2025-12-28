using Microsoft.AspNetCore.Mvc;
using TextOps.Channels.DevApi.Dtos;
using TextOps.Contracts.Orchestration;

namespace TextOps.Channels.DevApi.Controllers;

[ApiController]
[Route("runs")]
public sealed class RunsController : ControllerBase
{
    private readonly IRunOrchestrator _orchestrator;

    public RunsController(IRunOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet("{runId}")]
    [ProducesResponseType(typeof(TimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimeline(string runId)
    {
        try
        {
            var timeline = await _orchestrator.GetTimelineAsync(runId);
            var response = MapTimelineToResponse(timeline);
            return Ok(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(CreateNotFoundResponse(runId));
        }
    }

    private static TimelineResponse MapTimelineToResponse(RunTimeline timeline)
    {
        return new TimelineResponse
        {
            Run = new RunDto
            {
                RunId = timeline.Run.RunId,
                JobKey = timeline.Run.JobKey,
                Status = timeline.Run.Status.ToString(),
                CreatedAt = timeline.Run.CreatedAt,
                RequestedByAddress = timeline.Run.RequestedByAddress,
                ChannelId = timeline.Run.ChannelId,
                ConversationId = timeline.Run.ConversationId
            },
            Events = timeline.Events.Select(e => new RunEventDto
            {
                RunId = e.RunId,
                Type = e.Type,
                At = e.At,
                Actor = e.Actor,
                Payload = e.Payload
            }).ToList()
        };
    }

    private static ProblemDetails CreateNotFoundResponse(string runId)
    {
        return new ProblemDetails
        {
            Title = "Run Not Found",
            Detail = $"Run with ID '{runId}' was not found."
        };
    }
}

