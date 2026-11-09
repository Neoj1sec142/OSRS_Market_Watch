
using System.Net.Http.Json;
using MarketAPI.Models;

namespace MarketAPI.Services;

public sealed class OsrsWikiClient
{
    private readonly HttpClient _http;

    public OsrsWikiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<WikiItemDto>> GetItemsAsync(
        CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<List<WikiItemDto>>(
            "mapping", ct) ?? new();
    }

    public async Task<WikiLatestResponse> GetLatestAsync(
        CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<WikiLatestResponse>(
            "latest", ct)
            ?? throw new InvalidOperationException(
                "Wiki latest response was empty.");
    }

    public async Task<WikiAggregateResponse> GetFiveMinuteAsync(
        CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<WikiAggregateResponse>(
            "5m", ct)
            ?? throw new InvalidOperationException(
                "Wiki 5m response was empty.");
    }

    public async Task<WikiAggregateResponse> GetHourlyAsync(
        CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<WikiAggregateResponse>(
            "1h", ct)
            ?? throw new InvalidOperationException(
                "Wiki 1h response was empty.");
    }
}
