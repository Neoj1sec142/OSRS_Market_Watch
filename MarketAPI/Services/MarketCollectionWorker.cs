
using Microsoft.Extensions.Options;
using System.Diagnostics;
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
    private long _attemptCount;
    private long _failureCount;
    private int _consecutiveFailures;
    private DateTime? _lastSuccessUtc;
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
        var startedAtUtc = DateTime.UtcNow;
        var timer = Stopwatch.StartNew();

        var attempt = ++_attemptCount;

        try
        {
            using var scope = _scopeFactory.CreateScope();

            var service = scope.ServiceProvider
                .GetRequiredService<MarketIngestionService>();

            var result = await service.IngestAsync(ct);

            timer.Stop();

            var recoveredFailures = _consecutiveFailures;

            _consecutiveFailures = 0;
            _lastSuccessUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Scheduled ingestion complete. " +
                "Attempt={Attempt}, Batch={BatchId}, " +
                "Snapshots={Snapshots}, Aggregates={Aggregates}, " +
                "DurationMs={DurationMs}, LastSuccessUtc={LastSuccess}",
                attempt,
                result.CollectionBatchId,
                result.SnapshotsInserted,
                result.AggregatesAffected,
                timer.ElapsedMilliseconds,
                _lastSuccessUtc);

            if (recoveredFailures > 0)
            {
                _logger.LogInformation(
                    "Market collection recovered after " +
                    "{Failures} consecutive failed attempts.",
                    recoveredFailures);
            }
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Market collection canceled during shutdown.");
        }
        catch (Exception ex)
        {
            timer.Stop();

            _failureCount++;
            _consecutiveFailures++;

            _logger.LogError(
                ex,
                "Scheduled ingestion failed. " +
                "Attempt={Attempt}, " +
                "TotalFailures={TotalFailures}, " +
                "ConsecutiveFailures={ConsecutiveFailures}, " +
                "DurationMs={DurationMs}, " +
                "LastSuccessUtc={LastSuccess}",
                attempt,
                _failureCount,
                _consecutiveFailures,
                timer.ElapsedMilliseconds,
                _lastSuccessUtc);

            if (_consecutiveFailures >= 3)
            {
                _logger.LogWarning(
                    "Market collector degraded: " +
                    "{Count} consecutive failed attempts.",
                    _consecutiveFailures);
            }

            // The next scheduled tick makes another attempt.
            // We deliberately do not terminate the worker.
        }
    }

}
