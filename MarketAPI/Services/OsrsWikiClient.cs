
using System.Net;
using System.Net.Http.Json;
using MarketAPI.Models;

namespace MarketAPI.Services;

public sealed class OsrsWikiClient
{
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly ILogger<OsrsWikiClient> _logger;

    public OsrsWikiClient(
        HttpClient http,
        ILogger<OsrsWikiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<List<WikiItemDto>> GetItemsAsync(
        CancellationToken ct = default)
        => GetWithRetryAsync<List<WikiItemDto>>(
            "mapping", ct);

    public Task<WikiLatestResponse> GetLatestAsync(
        CancellationToken ct = default)
        => GetWithRetryAsync<WikiLatestResponse>(
            "latest", ct);

    public Task<WikiAggregateResponse> GetFiveMinuteAsync(
        CancellationToken ct = default)
        => GetWithRetryAsync<WikiAggregateResponse>(
            "5m", ct);

    public Task<WikiAggregateResponse> GetHourlyAsync(
        CancellationToken ct = default)
        => GetWithRetryAsync<WikiAggregateResponse>(
            "1h", ct);

    public Task<WikiAggregateResponse> GetHourlyAtAsync(
        DateTime bucketStartUtc,
        CancellationToken ct = default)
    {
        if (bucketStartUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException(
                "Expected a UTC timestamp.",
                nameof(bucketStartUtc));

        var utc = DateTime.SpecifyKind(
            bucketStartUtc, DateTimeKind.Utc);

        if (utc.Ticks % TimeSpan.TicksPerHour != 0)
            throw new ArgumentException(
                "Timestamp must align to a UTC hour.",
                nameof(bucketStartUtc));

        var unixTimestamp =
            new DateTimeOffset(utc).ToUnixTimeSeconds();

        return GetWithRetryAsync<WikiAggregateResponse>(
            $"1h?timestamp={unixTimestamp}", ct);
    }

    private async Task<T> GetWithRetryAsync<T>(
        string endpoint,
        CancellationToken ct)
    {
        for (int attempt = 1;
             attempt <= MaxAttempts;
             attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(
                    endpoint,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                response.EnsureSuccessStatusCode();

                var result = await response.Content
                    .ReadFromJsonAsync<T>(
                        cancellationToken: ct);

                return result ??
                    throw new InvalidOperationException(
                        $"Empty Wiki response: {endpoint}");
            }
            catch (HttpRequestException ex)
                when (!ct.IsCancellationRequested &&
                      attempt < MaxAttempts &&
                      (ex.StatusCode == null ||
                       IsTransient(ex.StatusCode.Value)))
            {
                await DelayBeforeRetryAsync(
                    endpoint, attempt, ex, ct);
            }
            catch (OperationCanceledException ex)
                when (!ct.IsCancellationRequested &&
                      attempt < MaxAttempts)
            {
                await DelayBeforeRetryAsync(
                    endpoint, attempt, ex, ct);
            }
        }

        // The final attempt propagates its original exception.
        throw new InvalidOperationException(
            $"Unexpected retry state for {endpoint}.");
    }

    private async Task DelayBeforeRetryAsync(
        string endpoint,
        int attempt,
        Exception exception,
        CancellationToken ct)
    {
        // Attempt 1: about 1–1.5 seconds
        // Attempt 2: about 2–2.5 seconds
        var baseDelayMs = 1000 * (1 << (attempt - 1));
        var jitterMs = Random.Shared.Next(0, 501);

        var delay = TimeSpan.FromMilliseconds(
            baseDelayMs + jitterMs);

        _logger.LogWarning(
            exception,
            "Wiki request {Endpoint} failed. " +
            "Attempt {Attempt}/{MaxAttempts}. " +
            "Retrying in {DelayMs}ms.",
            endpoint,
            attempt,
            MaxAttempts,
            delay.TotalMilliseconds);

        await Task.Delay(delay, ct);
    }

    private static bool IsTransient(
        HttpStatusCode status)
    {
        var code = (int)status;

        return status == HttpStatusCode.RequestTimeout ||
               code == 429 ||
               code >= 500;
    }
}
