using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Messaging;

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
                var processed = await PollForWork(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await HandleExecutionError(ex, stoppingToken);
            }
        }

        _logger.LogInformation("Execution service stopped");
    }

    private async Task<bool> PollForWork(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
        
        var queued = await queue.ClaimNextAsync(_workerId, stoppingToken);
        if (queued == null)
        {
            return false;
        }

        await ProcessDispatchAsync(scope, queue, queued, stoppingToken);
        return true;
    }

    private async Task HandleExecutionError(Exception ex, CancellationToken stoppingToken)
    {
        _logger.LogError(ex, "Error in execution service. Retrying in 5 seconds...");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
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
            LogDispatchStart(queued);
            var dispatch = new ExecutionDispatch(queued.RunId, queued.JobKey);
            var result = await workerExecutor.ExecuteAsync(dispatch, stoppingToken);

            LogOutboundMessages(result.Outbound);
            await HandleDispatchCompletion(queue, queued.QueueId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await HandleDispatchCancellation(queue, queued, stoppingToken);
        }
        catch (Exception ex)
        {
            await HandleDispatchError(queue, queued, ex, stoppingToken);
        }
    }

    private void LogDispatchStart(QueuedDispatch queued)
    {
        _logger.LogInformation(
            "Processing execution dispatch: QueueId={QueueId}, RunId={RunId}, JobKey={JobKey}",
            queued.QueueId, queued.RunId, queued.JobKey);
    }

    private void LogOutboundMessages(IReadOnlyList<OutboundMessage> outbound)
    {
        foreach (var message in outbound)
        {
            _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", message.ChannelId, message.Body);
        }
    }

    private static async Task HandleDispatchCompletion(IExecutionQueue queue, long queueId, CancellationToken stoppingToken)
    {
        await queue.CompleteAsync(queueId, success: true, error: null, stoppingToken);
    }

    private async Task HandleDispatchCancellation(IExecutionQueue queue, QueuedDispatch queued, CancellationToken stoppingToken)
    {
        _logger.LogWarning("Execution cancelled for dispatch: RunId={RunId}", queued.RunId);
        await queue.ReleaseAsync(queued.QueueId, "Cancelled due to shutdown", stoppingToken);
    }

    private async Task HandleDispatchError(IExecutionQueue queue, QueuedDispatch queued, Exception ex, CancellationToken stoppingToken)
    {
        _logger.LogError(ex, "Error processing execution dispatch: RunId={RunId}", queued.RunId);
        await queue.CompleteAsync(queued.QueueId, success: false, error: ex.Message, stoppingToken);
    }
}
