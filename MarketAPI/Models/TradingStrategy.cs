namespace MarketAPI.Models;
public class TradingStrategy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public long MaxCapitalPerTradeGp { get; set; }
    public long MaxTotalCapitalGp { get; set; }

    public decimal MinimumExpectedRoiPct { get; set; }
    public decimal MinimumConfidenceScore { get; set; }

    public long MinimumHourlyVolume { get; set; }
    public int MaximumQuoteAgeMinutes { get; set; }

    public int MaximumHoldingMinutes { get; set; }

    // Strategy-specific options without schema changes
    public string? ConfigurationJson { get; set; }
}