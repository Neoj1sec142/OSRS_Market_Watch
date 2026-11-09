
namespace MarketAPI.Services;

public sealed class MarketRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketRetentionWorker> _logger;

    public MarketRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MarketRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(15));

        try
        {
            while (await timer.WaitForNextTickAsync(
                stoppingToken))
            {
                try
                {
                    using var scope =
                        _scopeFactory.CreateScope();

                    var retention = scope.ServiceProvider
                        .GetRequiredService<MarketRetentionService>();

                    await retention.RunAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Market retention failed.");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }
}
