namespace MarketAPI.Models;

public class MarketPrediction
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public Guid PredictionModelId { get; set; }
    public PredictionModel PredictionModel { get; set; } = null!;

    public long FeatureSnapshotId { get; set; }
    public MarketFeatureSnapshot FeatureSnapshot { get; set; } = null!;

    public DateTime PredictedAtUtc { get; set; }
    public DateTime TargetTimeUtc { get; set; }

    public long? ReferencePriceGp { get; set; }

    // Predicted future market values
    public decimal? PredictedBuyPrice { get; set; }
    public decimal? PredictedSellPrice { get; set; }

    public decimal? PredictedPriceChangePct { get; set; }

    // Estimated trade characteristics
    public long? ExpectedProfitGp { get; set; }
    public decimal? ExpectedRoiPct { get; set; }

    public decimal? ProbabilityOfProfit { get; set; }
    public decimal? ProbabilityOfFill { get; set; }

    // Evaluation, populated only after target time
    public decimal? ActualTargetPrice { get; set; }
    public decimal? ActualPriceChangePct { get; set; }
    public decimal? PredictionError { get; set; }

    public DateTime? EvaluatedAtUtc { get; set; }
}