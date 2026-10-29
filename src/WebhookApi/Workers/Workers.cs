using WebhookApi.Services;

namespace WebhookApi.Workers;

/// <summary>Pops delivery ids from the Redis queue and dispatches them one at a time.</summary>
public class DispatchWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IQueueStore _queue;
    private readonly ILogger<DispatchWorker> _log;

    public DispatchWorker(IServiceScopeFactory scopes, IQueueStore queue, ILogger<DispatchWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Dispatch worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var id = await _queue.PopAsync();
                if (id is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();
                await processor.ProcessAsync(id.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("Dispatch worker error: {Message}", ex.Message);
                await DelayQuietly(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}

/// <summary>Every 30 seconds, re-queues pending deliveries whose next_retry_at has passed.</summary>
public class RetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetryWorker> _log;

    public RetryWorker(IServiceScopeFactory scopes, ILogger<RetryWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Retry worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                using var scope = _scopes.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<RetryScanner>();
                var count = await scanner.RequeueDueAsync(stoppingToken);
                if (count > 0) _log.LogInformation("Re-queued {Count} deliveries for retry", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("Retry worker error: {Message}", ex.Message);
            }
        }
    }
}
