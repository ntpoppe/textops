using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TextOps.Contracts.Execution;

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

        // Start stale lock recovery task
        var recoveryTask = RunStaleLockRecoveryAsync(stoppingToken);

        // Main polling loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await TryProcessNextAsync(stoppingToken);
                
                if (!processed)
                {
                    // No work available, wait before polling again
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in worker loop. Retrying in {Delay}...", _options.ErrorRetryDelay);
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

        await recoveryTask;
        _logger.LogInformation("Worker stopped: WorkerId={WorkerId}", _workerId);
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
        var executor = scope.ServiceProvider.GetRequiredService<IWorkerExecutor>();

        var queued = await queue.ClaimNextAsync(_workerId, ct);
        if (queued == null)
            return false;

        _logger.LogInformation(
            "Processing dispatch: QueueId={QueueId}, RunId={RunId}, JobKey={JobKey}, Attempt={Attempt}",
            queued.QueueId, queued.RunId, queued.JobKey, queued.Attempts);

        try
        {
            var dispatch = new ExecutionDispatch(queued.RunId, queued.JobKey);
            var result = await executor.ExecuteAsync(dispatch, ct);

            // Log outbound messages (in production, route to channel adapters)
            foreach (var outbound in result.Outbound)
            {
                _logger.LogInformation("OUTBOUND ({ChannelId}): {Body}", outbound.ChannelId, outbound.Body);
            }

            await queue.CompleteAsync(queued.QueueId, success: true, error: null, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown requested - release back to queue for another worker
            _logger.LogWarning("Execution cancelled, releasing: RunId={RunId}", queued.RunId);
            await queue.ReleaseAsync(queued.QueueId, "Cancelled due to shutdown", ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execution failed: RunId={RunId}", queued.RunId);

            if (queued.Attempts >= _options.MaxAttempts)
            {
                _logger.LogError(
                    "Max attempts reached, marking failed: RunId={RunId}, Attempts={Attempts}",
                    queued.RunId, queued.Attempts);
                await queue.CompleteAsync(queued.QueueId, success: false, error: ex.Message, ct);
            }
            else
            {
                await queue.ReleaseAsync(queued.QueueId, ex.Message, ct);
            }
            return true;
        }
    }

    private async Task RunStaleLockRecoveryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.StaleLockCheckInterval, ct);

                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IExecutionQueue>();
                
                var reclaimed = await queue.ReclaimStaleAsync(_options.LockTimeout, ct);
                if (reclaimed > 0)
                {
                    _logger.LogWarning("Reclaimed {Count} stale queue entries", reclaimed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in stale lock recovery");
            }
        }
    }
}

