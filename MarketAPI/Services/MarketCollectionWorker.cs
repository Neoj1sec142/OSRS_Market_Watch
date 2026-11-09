
using Microsoft.Extensions.Options;

namespace MarketAPI.Services;

public sealed class MarketCollectorOptions
{
    public bool Enabled { get; set; } = false;

    public int IntervalMinutes { get; set; } = 5;

    public bool RunImmediately { get; set; } = true;
}

public sealed class MarketCollectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MarketCollectorOptions> _options;
    private readonly ILogger<MarketCollectionWorker> _logger;

    public MarketCollectionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MarketCollectorOptions> options,
        ILogger<MarketCollectionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var settings = _options.Value;

        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "Market collector is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(
            Math.Max(1, settings.IntervalMinutes));

        _logger.LogInformation(
            "Market collector started. Interval={Interval}",
            interval);

        if (settings.RunImmediately)
            await CollectSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(
                stoppingToken))
            {
                await CollectSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Market collector shutting down.");
        }
    }

    private async Task CollectSafelyAsync(
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var service = scope.ServiceProvider
                .GetRequiredService<MarketIngestionService>();

            var result = await service.IngestAsync(ct);

            _logger.LogInformation(
                "Scheduled ingestion complete. " +
                "Batch={BatchId}, Snapshots={Snapshots}, " +
                "Aggregates={Aggregates}",
                result.CollectionBatchId,
                result.SnapshotsInserted,
                result.AggregatesAffected);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Scheduled market ingestion failed.");

            // Let the next timer tick retry.
        }
    }
}
