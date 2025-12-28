using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Messaging;

namespace TextOps.Worker;

/// <summary>
/// Background service that polls the database queue for work.
/// Handles claiming, executing, completing, and stale lock recovery.
/// </summary>
public sealed class WorkerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkerHostedService> _logger;
    private readonly WorkerOptions _options;
    private readonly string _workerId;

    public WorkerHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<WorkerOptions> options,
        ILogger<WorkerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _workerId = _options.WorkerId ?? $"worker-{Environment.MachineName}-{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started: WorkerId={WorkerId}", _workerId);

        var recoveryTask = StartRecoveryTask(stoppingToken);

        await PollForWork(stoppingToken);

        await recoveryTask;
        _logger.LogInformation("Worker stopped: WorkerId={WorkerId}", _workerId);
    }

    private Task StartRecoveryTask(CancellationToken stoppingToken)
    {
        return RunStaleLockRecoveryAsync(stoppingToken);
    }

    private async Task PollForWork(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await TryProcessNextAsync(stoppingToken);
                
                if (!processed)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error in worker loop. Retrying in {Delay}...", _options.ErrorRetryDelay);
                try
                {
                    await Task.Delay(_options.ErrorRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var executionQueue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
        var workerExecutor = scope.ServiceProvider.GetRequiredService<IWorkerExecutor>();

        var queuedDispatch = await executionQueue.ClaimNextAsync(_workerId, cancellationToken);
        if (queuedDispatch == null)
            return false;

        LogDispatchStart(queuedDispatch);

        try
        {
            var executionDispatch = new ExecutionDispatch(queuedDispatch.RunId, queuedDispatch.JobKey);
            var orchestratorResult = await workerExecutor.ExecuteAsync(executionDispatch, cancellationToken);

            LogOutboundMessages(orchestratorResult.Outbound);
            await HandleExecutionSuccess(executionQueue, queuedDispatch.QueueId, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await HandleExecutionCancellation(executionQueue, queuedDispatch, cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            await HandleExecutionFailure(executionQueue, queuedDispatch, exception, cancellationToken);
            return true;
        }
    }

    private void LogDispatchStart(QueuedDispatch queuedDispatch)
    {
        _logger.LogInformation(
            "Processing dispatch: QueueId={QueueId}, RunId={RunId}, JobKey={JobKey}, Attempt={Attempt}",
            queuedDispatch.QueueId, queuedDispatch.RunId, queuedDispatch.JobKey, queuedDispatch.Attempts);
    }

    private void LogOutboundMessages(IReadOnlyList<OutboundMessage> outboundMessages)
    {
        foreach (var outboundMessage in outboundMessages)
        {
            _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", outboundMessage.ChannelId, outboundMessage.Body);
        }
    }

    private static async Task HandleExecutionSuccess(IExecutionQueue executionQueue, long queueId, CancellationToken cancellationToken)
    {
        await executionQueue.CompleteAsync(queueId, success: true, errorMessage: null, cancellationToken);
    }

    private async Task HandleExecutionCancellation(IExecutionQueue executionQueue, QueuedDispatch queuedDispatch, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Execution cancelled, releasing: RunId={RunId}", queuedDispatch.RunId);
        await executionQueue.ReleaseAsync(queuedDispatch.QueueId, "Cancelled due to shutdown", cancellationToken);
    }

    private async Task HandleExecutionFailure(IExecutionQueue executionQueue, QueuedDispatch queuedDispatch, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Execution failed: RunId={RunId}", queuedDispatch.RunId);

        if (ShouldMarkAsFailed(queuedDispatch))
        {
            _logger.LogError(
                "Max attempts reached, marking failed: RunId={RunId}, Attempts={Attempts}",
                queuedDispatch.RunId, queuedDispatch.Attempts);
            await executionQueue.CompleteAsync(queuedDispatch.QueueId, success: false, errorMessage: exception.Message, cancellationToken);
        }
        else
        {
            await executionQueue.ReleaseAsync(queuedDispatch.QueueId, exception.Message, cancellationToken);
        }
    }

    private bool ShouldMarkAsFailed(QueuedDispatch queuedDispatch)
        => queuedDispatch.Attempts >= _options.MaxAttempts;

    private async Task RunStaleLockRecoveryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.StaleLockCheckInterval, cancellationToken);

                using var scope = _scopeFactory.CreateScope();
                var executionQueue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
                
                var reclaimedCount = await executionQueue.ReclaimStaleAsync(_options.LockTimeout, cancellationToken);
                if (reclaimedCount > 0)
                {
                    _logger.LogWarning("Reclaimed {Count} stale queue entries", reclaimedCount);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error in stale lock recovery");
            }
        }
    }
}

