namespace MarketAPI.Models;

public class BacktestRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TradingStrategyId { get; set; }
    public TradingStrategy TradingStrategy { get; set; } = null!;

    public Guid? PredictionModelId { get; set; }
    public PredictionModel? PredictionModel { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public DateTime DataFromUtc { get; set; }
    public DateTime DataToUtc { get; set; }

    public long StartingCapitalGp { get; set; }
    public long EndingCapitalGp { get; set; }

    public long NetProfitGp { get; set; }
    public long TotalTaxGp { get; set; }

    public int TradesExecuted { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }

    public decimal? WinRate { get; set; }
    public decimal? ReturnOnInvestmentPct { get; set; }
    public decimal? MaxDrawdownPct { get; set; }

    // Snapshot of the assumptions and parameters used
    public string? ConfigurationJson { get; set; }
    public string? ResultsJson { get; set; }
}