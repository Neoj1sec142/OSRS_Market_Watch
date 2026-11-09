
using MarketAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Data;

public class MarketDbContext : DbContext
{
    public MarketDbContext(DbContextOptions<MarketDbContext> options)
        : base(options)
    {
    }

    // Market data
    public DbSet<MarketItem> MarketItems => Set<MarketItem>();
    public DbSet<MarketCollectionBatch> MarketCollectionBatches
        => Set<MarketCollectionBatch>();
    public DbSet<MarketPriceSnapshot> MarketPriceSnapshots
        => Set<MarketPriceSnapshot>();
    public DbSet<MarketPriceAggregate> MarketPriceAggregates
        => Set<MarketPriceAggregate>();

    // Analytics
    public DbSet<MarketFeatureSnapshot> MarketFeatureSnapshots
        => Set<MarketFeatureSnapshot>();

    // Machine learning
    public DbSet<PredictionModel> PredictionModels
        => Set<PredictionModel>();
    public DbSet<MarketPrediction> MarketPredictions
        => Set<MarketPrediction>();
    public DbSet<MarketSignal> MarketSignals
        => Set<MarketSignal>();

    // Trading
    public DbSet<TradingStrategy> TradingStrategies
        => Set<TradingStrategy>();
    public DbSet<TradePosition> TradePositions
        => Set<TradePosition>();
    public DbSet<TradeExecution> TradeExecutions
        => Set<TradeExecution>();

    // Backtesting and dashboard
    public DbSet<BacktestRun> BacktestRuns
        => Set<BacktestRun>();
    public DbSet<MarketWatchlist> MarketWatchlists
        => Set<MarketWatchlist>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureMarketItems(modelBuilder);
        ConfigureMarketCollectionBatches(modelBuilder);
        ConfigureMarketPriceSnapshots(modelBuilder);
        ConfigureMarketPriceAggregates(modelBuilder);

        ConfigureMarketFeatureSnapshots(modelBuilder);

        ConfigurePredictionModels(modelBuilder);
        ConfigureMarketPredictions(modelBuilder);
        ConfigureMarketSignals(modelBuilder);

        ConfigureTradingStrategies(modelBuilder);
        ConfigureTradePositions(modelBuilder);
        ConfigureTradeExecutions(modelBuilder);

        ConfigureBacktestRuns(modelBuilder);
        ConfigureMarketWatchlists(modelBuilder);
    }

    #region Market Data

    private static void ConfigureMarketItems(ModelBuilder builder)
    {
        builder.Entity<MarketItem>(entity =>
        {
            entity.ToTable("MarketItems");

            // Jagex item ID; never database-generated.
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Examine)
                .HasMaxLength(2000);

            entity.Property(x => x.Icon)
                .HasMaxLength(500);

            entity.Property(x => x.IsActive)
                .HasDefaultValue(true);

            entity.Property(x => x.IsTaxExempt)
                .HasDefaultValue(false);

            entity.HasIndex(x => x.Name);
            entity.HasIndex(x => x.IsActive);
        });
    }

    private static void ConfigureMarketCollectionBatches(
        ModelBuilder builder)
    {
        builder.Entity<MarketCollectionBatch>(entity =>
        {
            entity.ToTable("MarketCollectionBatches");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Source)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.Status)
                .HasConversion<int>();

            entity.Property(x => x.ErrorMessage)
                .HasMaxLength(4000);

            entity.HasIndex(x => x.StartedAtUtc);
            entity.HasIndex(x => x.Status);
        });
    }

    private static void ConfigureMarketPriceSnapshots(
        ModelBuilder builder)
    {
        builder.Entity<MarketPriceSnapshot>(entity =>
        {
            entity.ToTable("MarketPriceSnapshots");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany(x => x.PriceSnapshots)
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.CollectionBatch)
                .WithMany()
                .HasForeignKey(x => x.CollectionBatchId)
                .OnDelete(DeleteBehavior.Restrict);

            // One observation per item per collection batch.
            entity.HasIndex(x => new
            {
                x.CollectionBatchId,
                x.MarketItemId
            }).IsUnique();

            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.CollectedAtUtc
            });

            entity.HasIndex(x => x.CollectedAtUtc);
        });
    }

    private static void ConfigureMarketPriceAggregates(
        ModelBuilder builder)
    {
        builder.Entity<MarketPriceAggregate>(entity =>
        {
            entity.ToTable("MarketPriceAggregates");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany(x => x.PriceAggregates)
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.Interval)
                .HasConversion<int>();

            entity.Property(x => x.AverageInstantBuyPrice)
                .HasPrecision(20, 6);

            entity.Property(x => x.AverageInstantSellPrice)
                .HasPrecision(20, 6);

            // Support idempotent imports of API time buckets.
            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.Interval,
                x.BucketStartUtc
            }).IsUnique();

            entity.HasIndex(x => new
            {
                x.Interval,
                x.BucketStartUtc
            });
        });
    }

    #endregion

    #region Analytics

    private static void ConfigureMarketFeatureSnapshots(
        ModelBuilder builder)
    {
        builder.Entity<MarketFeatureSnapshot>(entity =>
        {
            entity.ToTable("MarketFeatureSnapshots");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany()
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.FeatureVersion)
                .IsRequired()
                .HasMaxLength(50);

            // Percentage and financial feature precision.
            entity.Property(x => x.SpreadPercent)
                .HasPrecision(20, 8);

            entity.Property(x => x.EstimatedRoiPercent)
                .HasPrecision(20, 8);

            entity.Property(x => x.PriceChange1HourPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.PriceChange6HoursPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.PriceChange24HoursPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.Sma1Hour)
                .HasPrecision(20, 6);

            entity.Property(x => x.Sma24Hours)
                .HasPrecision(20, 6);

            entity.Property(x => x.Ema1Hour)
                .HasPrecision(20, 6);

            entity.Property(x => x.Ema24Hours)
                .HasPrecision(20, 6);

            entity.Property(x => x.Volatility24Hours)
                .HasPrecision(20, 8);

            entity.Property(x => x.VolumeChangePercent)
                .HasPrecision(20, 8);

            entity.Property(x => x.BuySellVolumeRatio)
                .HasPrecision(20, 8);

            entity.Property(x => x.BuyQuoteAgeMinutes)
                .HasPrecision(20, 4);

            entity.Property(x => x.SellQuoteAgeMinutes)
                .HasPrecision(20, 4);

            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.FeatureTimeUtc,
                x.FeatureVersion
            }).IsUnique();

            entity.HasIndex(x => new
            {
                x.FeatureVersion,
                x.FeatureTimeUtc
            });
        });
    }

    #endregion

    #region Machine Learning

    private static void ConfigurePredictionModels(
        ModelBuilder builder)
    {
        builder.Entity<PredictionModel>(entity =>
        {
            entity.ToTable("PredictionModels");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Version)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(x => x.Algorithm)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.FeatureVersion)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(x => x.TargetName)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(x => x.ArtifactPath)
                .HasMaxLength(1000);

            entity.Property(x => x.ValidationMae)
                .HasPrecision(20, 8);

            entity.Property(x => x.ValidationRmse)
                .HasPrecision(20, 8);

            entity.HasIndex(x => new
            {
                x.Name,
                x.Version
            }).IsUnique();

            entity.HasIndex(x => x.IsActive);
        });
    }

    private static void ConfigureMarketPredictions(
        ModelBuilder builder)
    {
        builder.Entity<MarketPrediction>(entity =>
        {
            entity.ToTable("MarketPredictions");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany()
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.PredictionModel)
                .WithMany(x => x.Predictions)
                .HasForeignKey(x => x.PredictionModelId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.FeatureSnapshot)
                .WithMany()
                .HasForeignKey(x => x.FeatureSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.PredictedBuyPrice)
                .HasPrecision(20, 6);

            entity.Property(x => x.PredictedSellPrice)
                .HasPrecision(20, 6);

            entity.Property(x => x.PredictedPriceChangePct)
                .HasPrecision(20, 8);

            entity.Property(x => x.ExpectedRoiPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.ProbabilityOfProfit)
                .HasPrecision(12, 8);

            entity.Property(x => x.ProbabilityOfFill)
                .HasPrecision(12, 8);

            entity.Property(x => x.ActualTargetPrice)
                .HasPrecision(20, 6);

            entity.Property(x => x.ActualPriceChangePct)
                .HasPrecision(20, 8);

            entity.Property(x => x.PredictionError)
                .HasPrecision(20, 8);

            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.PredictedAtUtc
            });

            entity.HasIndex(x => new
            {
                x.PredictionModelId,
                x.TargetTimeUtc
            });

            entity.HasIndex(x => new
            {
                x.TargetTimeUtc,
                x.EvaluatedAtUtc
            });

            entity.HasIndex(x => x.FeatureSnapshotId);
        });
    }

    private static void ConfigureMarketSignals(
        ModelBuilder builder)
    {
        builder.Entity<MarketSignal>(entity =>
        {
            entity.ToTable("MarketSignals");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany()
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.MarketPrediction)
                .WithMany()
                .HasForeignKey(x => x.MarketPredictionId)
                .OnDelete(DeleteBehavior.Restrict);

            // MarketSignal has a Strategy ID but no
            // corresponding navigation property.
            entity.HasOne<TradingStrategy>()
                .WithMany()
                .HasForeignKey(x => x.TradingStrategyId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.Action)
                .HasConversion<int>();

            entity.Property(x => x.Status)
                .HasConversion<int>();

            entity.Property(x => x.ConfidenceScore)
                .HasPrecision(12, 8);

            entity.Property(x => x.Reason)
                .HasMaxLength(2000);

            entity.HasIndex(x => new
            {
                x.Status,
                x.GeneratedAtUtc
            });

            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.GeneratedAtUtc
            });
        });
    }

    #endregion

    #region Trading

    private static void ConfigureTradingStrategies(
        ModelBuilder builder)
    {
        builder.Entity<TradingStrategy>(entity =>
        {
            entity.ToTable("TradingStrategies");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Description)
                .HasMaxLength(2000);

            entity.Property(x => x.MinimumExpectedRoiPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.MinimumConfidenceScore)
                .HasPrecision(12, 8);

            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.IsEnabled);
        });
    }

    private static void ConfigureTradePositions(
        ModelBuilder builder)
    {
        builder.Entity<TradePosition>(entity =>
        {
            entity.ToTable("TradePositions");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany()
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.TradingStrategy)
                .WithMany()
                .HasForeignKey(x => x.TradingStrategyId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.MarketSignal)
                .WithMany()
                .HasForeignKey(x => x.MarketSignalId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.Mode)
                .HasConversion<int>();

            entity.Property(x => x.Status)
                .HasConversion<int>();

            entity.HasIndex(x => new
            {
                x.Mode,
                x.Status,
                x.OpenedAtUtc
            });

            entity.HasIndex(x => new
            {
                x.MarketItemId,
                x.OpenedAtUtc
            });
        });
    }

    private static void ConfigureTradeExecutions(
        ModelBuilder builder)
    {
        builder.Entity<TradeExecution>(entity =>
        {
            entity.ToTable("TradeExecutions");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.TradePosition)
                .WithMany(x => x.Executions)
                .HasForeignKey(x => x.TradePositionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.Side)
                .HasConversion<int>();

            entity.Property(x => x.Source)
                .HasConversion<int>();

            entity.HasIndex(x => new
            {
                x.TradePositionId,
                x.ExecutedAtUtc
            });
        });
    }

    #endregion

    #region Backtesting & Dashboard

    private static void ConfigureBacktestRuns(
        ModelBuilder builder)
    {
        builder.Entity<BacktestRun>(entity =>
        {
            entity.ToTable("BacktestRuns");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.TradingStrategy)
                .WithMany()
                .HasForeignKey(x => x.TradingStrategyId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.PredictionModel)
                .WithMany()
                .HasForeignKey(x => x.PredictionModelId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.WinRate)
                .HasPrecision(12, 8);

            entity.Property(x => x.ReturnOnInvestmentPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.MaxDrawdownPct)
                .HasPrecision(20, 8);

            entity.HasIndex(x => x.StartedAtUtc);

            entity.HasIndex(x => new
            {
                x.TradingStrategyId,
                x.StartedAtUtc
            });
        });
    }

    private static void ConfigureMarketWatchlists(
        ModelBuilder builder)
    {
        builder.Entity<MarketWatchlist>(entity =>
        {
            entity.ToTable("MarketWatchlists");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.MarketItem)
                .WithMany()
                .HasForeignKey(x => x.MarketItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(x => x.MinimumProfitMarginPct)
                .HasPrecision(20, 8);

            entity.Property(x => x.Notes)
                .HasMaxLength(2000);

            // One watchlist entry per item in this
            // single-user application.
            entity.HasIndex(x => x.MarketItemId)
                .IsUnique();
        });
    }

    #endregion
}
