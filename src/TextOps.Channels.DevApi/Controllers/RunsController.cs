using Microsoft.AspNetCore.Mvc;
using TextOps.Channels.DevApi.Dtos;
using TextOps.Orchestrator.Orchestration;

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
    public IActionResult GetTimeline(string runId)
    {
        try
        {
            var timeline = _orchestrator.GetTimeline(runId);

            var response = new TimelineResponse
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

            return Ok(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Run Not Found",
                Detail = $"Run with ID '{runId}' was not found."
            });
        }
    }
}

