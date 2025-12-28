using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;

namespace TextOps.Execution;

/// <summary>
/// Background service that processes execution dispatch requests from the queue.
/// Used with InMemoryExecutionQueue for in-process development.
/// For distributed workers, use TextOps.Worker with DatabaseExecutionQueue instead.
/// </summary>
public sealed class ExecutionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionHostedService> _logger;
    private readonly string _workerId;

    public ExecutionHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = $"devapi-{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Execution service started: WorkerId={WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
                
                var queued = await queue.ClaimNextAsync(_workerId, stoppingToken);
                if (queued == null)
                {
                    // No work available, wait before polling again
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                    continue;
                }

                await ProcessDispatchAsync(scope, queue, queued, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in execution service. Retrying in 5 seconds...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Execution service stopped");
    }

    private async Task ProcessDispatchAsync(
        IServiceScope scope, 
        IExecutionQueue queue, 
        QueuedDispatch queued, 
        CancellationToken stoppingToken)
    {
        var workerExecutor = scope.ServiceProvider.GetRequiredService<IWorkerExecutor>();

        try
        {
            _logger.LogInformation(
                "Processing execution dispatch: QueueId={QueueId}, RunId={RunId}, JobKey={JobKey}",
                queued.QueueId, queued.RunId, queued.JobKey);

            var dispatch = new ExecutionDispatch(queued.RunId, queued.JobKey);
            var result = await workerExecutor.ExecuteAsync(dispatch, stoppingToken);

            // Log outbound messages
            foreach (var outbound in result.Outbound)
            {
                _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", outbound.ChannelId, outbound.Body);
            }

            await queue.CompleteAsync(queued.QueueId, success: true, error: null, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Execution cancelled for dispatch: RunId={RunId}", queued.RunId);
            await queue.ReleaseAsync(queued.QueueId, "Cancelled due to shutdown", stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing execution dispatch: RunId={RunId}", queued.RunId);
            await queue.CompleteAsync(queued.QueueId, success: false, error: ex.Message, stoppingToken);
        }
    }
}
