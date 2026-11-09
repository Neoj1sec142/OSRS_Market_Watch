
namespace MarketAPI.Models;

public sealed class MarketOpportunityQuery
{
    public long BudgetGp { get; set; } = 10_000_000;

    public long MinHourlyVolume { get; set; } = 100;

    public long MinTotalProfitGp { get; set; } = 1_000;

    public decimal MinRoiPercent { get; set; } = 0.5m;

    public int MaxQuoteAgeMinutes { get; set; } = 30;

    // How much of the observed hourly volume
    // we're willing to assume might be accessible.
    public decimal VolumeParticipationRate { get; set; } = 0.05m;

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public string? Search { get; set; }
}

public sealed class MarketOpportunityDto
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;

    public int? BuyLimit { get; set; }

    public long ObservedInstantBuyPriceGp { get; set; }
    public long ObservedInstantSellPriceGp { get; set; }

    // Our estimated flip:
    // Buy at observed instant-sell price.
    // Sell at observed instant-buy price.
    public long EstimatedEntryPriceGp { get; set; }
    public long EstimatedExitPriceGp { get; set; }

    public long GrossSpreadGp { get; set; }
    public decimal GrossSpreadPercent { get; set; }

    public long TaxPerItemGp { get; set; }
    public long NetProfitPerItemGp { get; set; }
    public decimal RoiPercent { get; set; }

    public long HourlyInstantBuyVolume { get; set; }
    public long HourlyInstantSellVolume { get; set; }
    public long ConservativeHourlyVolume { get; set; }

    public int SuggestedQuantity { get; set; }
    public long EstimatedCapitalRequiredGp { get; set; }
    public long EstimatedTotalProfitGp { get; set; }

    public DateTime InstantBuyTimeUtc { get; set; }
    public DateTime InstantSellTimeUtc { get; set; }
    public DateTime VolumeBucketStartUtc { get; set; }

    public double InstantBuyAgeMinutes { get; set; }
    public double InstantSellAgeMinutes { get; set; }

    // Not an ML confidence score.
    public decimal LiquidityParticipationPercent { get; set; }
}
public enum MarketDataStatus
{
    Fresh = 0,
    Stale = 1,
    Unavailable = 2
}
public sealed class MarketOpportunityResponse
{
    public DateTime AnalyzedAtUtc { get; set; }

    public MarketDataStatus DataStatus { get; set; }
    public DateTime? LatestHourlyBucketUtc { get; set; }

    public string? DiagnosticMessage { get; set; }

    public long BudgetGp { get; set; }

    public int TotalCandidates { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }

    public IReadOnlyList<MarketOpportunityDto> Opportunities
        { get; set; } = [];
}
