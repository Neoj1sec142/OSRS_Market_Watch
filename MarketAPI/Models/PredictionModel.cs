namespace MarketAPI.Models;
public class PredictionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    // E.g. LightGBM, XGBoost, RandomForest
    public string Algorithm { get; set; } = string.Empty;

    public string FeatureVersion { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;

    public int PredictionHorizonMinutes { get; set; }

    // Location of serialized Python model
    public string? ArtifactPath { get; set; }

    public DateTime TrainingStartedUtc { get; set; }
    public DateTime? TrainingCompletedUtc { get; set; }

    public DateTime? TrainingDataFromUtc { get; set; }
    public DateTime? TrainingDataToUtc { get; set; }

    public decimal? ValidationMae { get; set; }
    public decimal? ValidationRmse { get; set; }

    public string? MetricsJson { get; set; }
    public string? HyperparametersJson { get; set; }

    public bool IsActive { get; set; }

    public ICollection<MarketPrediction> Predictions { get; set; }
        = new List<MarketPrediction>();
}
