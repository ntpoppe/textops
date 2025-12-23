using System.Threading.Channels;
using TextOps.Orchestrator.Orchestration;
using TextOps.Worker.Stub;

namespace TextOps.Channels.DevApi.Execution;

/// <summary>
/// Background service that processes execution dispatch requests from the queue.
/// </summary>
public sealed class ExecutionHostedService : BackgroundService
{
    private readonly ChannelReader<ExecutionDispatch> _queue;
    private readonly IWorkerExecutor _workerExecutor;
    private readonly IRunOrchestrator _orchestrator;
    private readonly ILogger<ExecutionHostedService> _logger;

    public ExecutionHostedService(
        InMemoryExecutionQueue queue,
        IWorkerExecutor workerExecutor,
        IRunOrchestrator orchestrator,
        ILogger<ExecutionHostedService> logger)
    {
        _queue = queue.Reader;
        _workerExecutor = workerExecutor;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var dispatch in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogInformation("Processing execution dispatch: RunId={RunId}, JobKey={JobKey}", dispatch.RunId, dispatch.JobKey);

                // Execute the work
                var result = await _workerExecutor.ExecuteAsync(dispatch, stoppingToken);

                // Process outbound messages from execution callbacks
                foreach (var outbound in result.Outbound)
                {
                    _logger.LogInformation("OUTBOUND (dev): {Body}", outbound.Body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing execution dispatch: RunId={RunId}", dispatch.RunId);
            }
        }
    }
}

