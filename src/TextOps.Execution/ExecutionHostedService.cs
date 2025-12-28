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
            catch (Exception exception)
            {
                await HandleExecutionError(exception, stoppingToken);
            }
        }

        _logger.LogInformation("Execution service stopped");
    }

    private async Task<bool> PollForWork(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var executionQueue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
        
        var queuedDispatch = await executionQueue.ClaimNextAsync(_workerId, stoppingToken);
        if (queuedDispatch == null)
        {
            return false;
        }

        await ProcessDispatchAsync(scope, executionQueue, queuedDispatch, stoppingToken);
        return true;
    }

    private async Task HandleExecutionError(Exception exception, CancellationToken stoppingToken)
    {
        _logger.LogError(exception, "Error in execution service. Retrying in 5 seconds...");
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
        IExecutionQueue executionQueue, 
        QueuedDispatch queuedDispatch, 
        CancellationToken stoppingToken)
    {
        var workerExecutor = scope.ServiceProvider.GetRequiredService<IWorkerExecutor>();

        try
        {
            LogDispatchStart(queuedDispatch);
            var executionDispatch = new ExecutionDispatch(queuedDispatch.RunId, queuedDispatch.JobKey);
            var orchestratorResult = await workerExecutor.ExecuteAsync(executionDispatch, stoppingToken);

            LogOutboundMessages(orchestratorResult.Outbound);
            await HandleDispatchCompletion(executionQueue, queuedDispatch.QueueId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await HandleDispatchCancellation(executionQueue, queuedDispatch, stoppingToken);
        }
        catch (Exception exception)
        {
            await HandleDispatchError(executionQueue, queuedDispatch, exception, stoppingToken);
        }
    }

    private void LogDispatchStart(QueuedDispatch queuedDispatch)
    {
        _logger.LogInformation(
            "Processing execution dispatch: QueueId={QueueId}, RunId={RunId}, JobKey={JobKey}",
            queuedDispatch.QueueId, queuedDispatch.RunId, queuedDispatch.JobKey);
    }

    private void LogOutboundMessages(IReadOnlyList<OutboundMessage> outboundMessages)
    {
        foreach (var outboundMessage in outboundMessages)
        {
            _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", outboundMessage.ChannelId, outboundMessage.Body);
        }
    }

    private static async Task HandleDispatchCompletion(IExecutionQueue executionQueue, long queueId, CancellationToken stoppingToken)
    {
        await executionQueue.CompleteAsync(queueId, success: true, errorMessage: null, stoppingToken);
    }

    private async Task HandleDispatchCancellation(IExecutionQueue executionQueue, QueuedDispatch queuedDispatch, CancellationToken stoppingToken)
    {
        _logger.LogWarning("Execution cancelled for dispatch: RunId={RunId}", queuedDispatch.RunId);
        await executionQueue.ReleaseAsync(queuedDispatch.QueueId, "Cancelled due to shutdown", stoppingToken);
    }

    private async Task HandleDispatchError(IExecutionQueue executionQueue, QueuedDispatch queuedDispatch, Exception exception, CancellationToken stoppingToken)
    {
        _logger.LogError(exception, "Error processing execution dispatch: RunId={RunId}", queuedDispatch.RunId);
        await executionQueue.CompleteAsync(queuedDispatch.QueueId, success: false, errorMessage: exception.Message, stoppingToken);
    }
}
