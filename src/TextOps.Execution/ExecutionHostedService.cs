using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;

namespace TextOps.Execution;

/// <summary>
/// Background service that processes execution dispatch requests from the queue.
/// </summary>
public sealed class ExecutionHostedService : BackgroundService
{
    private readonly IExecutionQueueReader _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionHostedService> _logger;

    public ExecutionHostedService(
        IExecutionQueueReader queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Execution service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var dispatch in _queue.ReadAllAsync(stoppingToken))
                {
                    await ProcessDispatchAsync(dispatch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown - don't log as error
                break;
            }
            catch (Exception ex)
            {
                // Queue/channel error - log and retry after delay
                _logger.LogError(ex, "Error reading from execution queue. Retrying in 5 seconds...");
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

    private async Task ProcessDispatchAsync(ExecutionDispatch dispatch, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var workerExecutor = scope.ServiceProvider.GetRequiredService<IWorkerExecutor>();

        try
        {
            _logger.LogInformation("Processing execution dispatch: RunId={RunId}, JobKey={JobKey}", dispatch.RunId, dispatch.JobKey);

            // Execute the work
            var result = await workerExecutor.ExecuteAsync(dispatch, stoppingToken);

            // Process outbound messages from execution callbacks
            // Note: In production, these would be sent via the appropriate channel adapter
            foreach (var outbound in result.Outbound)
            {
                _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", outbound.ChannelId, outbound.Body);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Execution cancelled for dispatch: RunId={RunId}", dispatch.RunId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing execution dispatch: RunId={RunId}", dispatch.RunId);
        }
    }
}
