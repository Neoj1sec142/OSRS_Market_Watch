
using System.Text.Json.Serialization;

namespace MarketAPI.Models;

public sealed class WikiItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("examine")]
    public string? Examine { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("members")]
    public bool Members { get; set; }

    [JsonPropertyName("limit")]
    public int? BuyLimit { get; set; }

    [JsonPropertyName("highalch")]
    public long? HighAlchValue { get; set; }

    [JsonPropertyName("lowalch")]
    public long? LowAlchValue { get; set; }
}

public sealed class WikiLatestResponse
{
    [JsonPropertyName("data")]
    public Dictionary<string, WikiLatestPriceDto> Data { get; set; }
        = new();
}

public sealed class WikiLatestPriceDto
{
    [JsonPropertyName("high")]
    public long? High { get; set; }

    [JsonPropertyName("highTime")]
    public long? HighTime { get; set; }

    [JsonPropertyName("low")]
    public long? Low { get; set; }

    [JsonPropertyName("lowTime")]
    public long? LowTime { get; set; }
}

public sealed class WikiAggregateResponse
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, WikiAggregateDto> Data { get; set; }
        = new();
}

public sealed class WikiAggregateDto
{
    [JsonPropertyName("avgHighPrice")]
    public decimal? AverageHighPrice { get; set; }

    [JsonPropertyName("avgLowPrice")]
    public decimal? AverageLowPrice { get; set; }

    [JsonPropertyName("highPriceVolume")]
    public long HighPriceVolume { get; set; }

    [JsonPropertyName("lowPriceVolume")]
    public long LowPriceVolume { get; set; }
}
