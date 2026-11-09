using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketAPI.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarketSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketCollectionBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsCollected = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketCollectionBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Examine = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Members = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuyLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    HighAlchValue = table.Column<long>(type: "INTEGER", nullable: true),
                    LowAlchValue = table.Column<long>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    IsTaxExempt = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LastMetadataUpdateUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PredictionModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    PredictionHorizonMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ArtifactPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TrainingStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainingCompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrainingDataFromUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrainingDataToUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ValidationMae = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    ValidationRmse = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: true),
                    HyperparametersJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictionModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingStrategies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxCapitalPerTradeGp = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxTotalCapitalGp = table.Column<long>(type: "INTEGER", nullable: false),
                    MinimumExpectedRoiPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: false),
                    MinimumConfidenceScore = table.Column<decimal>(type: "TEXT", precision: 12, scale: 8, nullable: false),
                    MinimumHourlyVolume = table.Column<long>(type: "INTEGER", nullable: false),
                    MaximumQuoteAgeMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumHoldingMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketFeatureSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeatureTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    InstantBuyPrice = table.Column<long>(type: "INTEGER", nullable: true),
                    InstantSellPrice = table.Column<long>(type: "INTEGER", nullable: true),
                    SpreadGp = table.Column<long>(type: "INTEGER", nullable: true),
                    SpreadPercent = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    EstimatedProfitPerItemGp = table.Column<long>(type: "INTEGER", nullable: true),
                    EstimatedRoiPercent = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    PriceChange1HourPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    PriceChange6HoursPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    PriceChange24HoursPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    Sma1Hour = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    Sma24Hours = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    Ema1Hour = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    Ema24Hours = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    Volatility24Hours = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    Volume1Hour = table.Column<long>(type: "INTEGER", nullable: true),
                    Volume24Hours = table.Column<long>(type: "INTEGER", nullable: true),
                    VolumeChangePercent = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    BuySellVolumeRatio = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    BuyQuoteAgeMinutes = table.Column<decimal>(type: "TEXT", precision: 20, scale: 4, nullable: true),
                    SellQuoteAgeMinutes = table.Column<decimal>(type: "TEXT", precision: 20, scale: 4, nullable: true),
                    ObservationsUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDataSufficient = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketFeatureSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketFeatureSnapshots_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MarketPriceAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    BucketStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AverageInstantBuyPrice = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    AverageInstantSellPrice = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    InstantBuyVolume = table.Column<long>(type: "INTEGER", nullable: false),
                    InstantSellVolume = table.Column<long>(type: "INTEGER", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketPriceAggregates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketPriceAggregates_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MarketPriceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionBatchId = table.Column<long>(type: "INTEGER", nullable: false),
                    InstantBuyPrice = table.Column<long>(type: "INTEGER", nullable: true),
                    InstantSellPrice = table.Column<long>(type: "INTEGER", nullable: true),
                    InstantBuyTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InstantSellTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CollectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketPriceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketPriceSnapshots_MarketCollectionBatches_CollectionBatchId",
                        column: x => x.CollectionBatchId,
                        principalTable: "MarketCollectionBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketPriceSnapshots_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MarketWatchlists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TargetBuyPriceGp = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetSellPriceGp = table.Column<long>(type: "INTEGER", nullable: true),
                    MinimumProfitMarginPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    AlertsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketWatchlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketWatchlists_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TradingStrategyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PredictionModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DataFromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DataToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartingCapitalGp = table.Column<long>(type: "INTEGER", nullable: false),
                    EndingCapitalGp = table.Column<long>(type: "INTEGER", nullable: false),
                    NetProfitGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTaxGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TradesExecuted = table.Column<int>(type: "INTEGER", nullable: false),
                    WinningTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    LosingTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinRate = table.Column<decimal>(type: "TEXT", precision: 12, scale: 8, nullable: true),
                    ReturnOnInvestmentPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    MaxDrawdownPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacktestRuns_PredictionModels_PredictionModelId",
                        column: x => x.PredictionModelId,
                        principalTable: "PredictionModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BacktestRuns_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MarketPredictions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    PredictionModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeatureSnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    PredictedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TargetTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReferencePriceGp = table.Column<long>(type: "INTEGER", nullable: true),
                    PredictedBuyPrice = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    PredictedSellPrice = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    PredictedPriceChangePct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    ExpectedProfitGp = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpectedRoiPct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    ProbabilityOfProfit = table.Column<decimal>(type: "TEXT", precision: 12, scale: 8, nullable: true),
                    ProbabilityOfFill = table.Column<decimal>(type: "TEXT", precision: 12, scale: 8, nullable: true),
                    ActualTargetPrice = table.Column<decimal>(type: "TEXT", precision: 20, scale: 6, nullable: true),
                    ActualPriceChangePct = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    PredictionError = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketPredictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketPredictions_MarketFeatureSnapshots_FeatureSnapshotId",
                        column: x => x.FeatureSnapshotId,
                        principalTable: "MarketFeatureSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketPredictions_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketPredictions_PredictionModels_PredictionModelId",
                        column: x => x.PredictionModelId,
                        principalTable: "PredictionModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MarketSignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    MarketPredictionId = table.Column<long>(type: "INTEGER", nullable: true),
                    TradingStrategyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuggestedBuyPriceGp = table.Column<long>(type: "INTEGER", nullable: true),
                    SuggestedSellPriceGp = table.Column<long>(type: "INTEGER", nullable: true),
                    SuggestedQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedTotalProfitGp = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "TEXT", precision: 12, scale: 8, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketSignals_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketSignals_MarketPredictions_MarketPredictionId",
                        column: x => x.MarketPredictionId,
                        principalTable: "MarketPredictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketSignals_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TradePositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MarketItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    TradingStrategyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MarketSignalId = table.Column<long>(type: "INTEGER", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QuantityBought = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantitySold = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBuyCostGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSaleRevenueGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTaxGp = table.Column<long>(type: "INTEGER", nullable: false),
                    RealizedProfitGp = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradePositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradePositions_MarketItems_MarketItemId",
                        column: x => x.MarketItemId,
                        principalTable: "MarketItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradePositions_MarketSignals_MarketSignalId",
                        column: x => x.MarketSignalId,
                        principalTable: "MarketSignals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradePositions_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TradeExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradePositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    PricePerItemGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalValueGp = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxPaidGp = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeExecutions_TradePositions_TradePositionId",
                        column: x => x.TradePositionId,
                        principalTable: "TradePositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_PredictionModelId",
                table: "BacktestRuns",
                column: "PredictionModelId");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_StartedAtUtc",
                table: "BacktestRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_TradingStrategyId_StartedAtUtc",
                table: "BacktestRuns",
                columns: new[] { "TradingStrategyId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketCollectionBatches_StartedAtUtc",
                table: "MarketCollectionBatches",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MarketCollectionBatches_Status",
                table: "MarketCollectionBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MarketFeatureSnapshots_FeatureVersion_FeatureTimeUtc",
                table: "MarketFeatureSnapshots",
                columns: new[] { "FeatureVersion", "FeatureTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketFeatureSnapshots_MarketItemId_FeatureTimeUtc_FeatureVersion",
                table: "MarketFeatureSnapshots",
                columns: new[] { "MarketItemId", "FeatureTimeUtc", "FeatureVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketItems_IsActive",
                table: "MarketItems",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MarketItems_Name",
                table: "MarketItems",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPredictions_FeatureSnapshotId",
                table: "MarketPredictions",
                column: "FeatureSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPredictions_MarketItemId_PredictedAtUtc",
                table: "MarketPredictions",
                columns: new[] { "MarketItemId", "PredictedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPredictions_PredictionModelId_TargetTimeUtc",
                table: "MarketPredictions",
                columns: new[] { "PredictionModelId", "TargetTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPredictions_TargetTimeUtc_EvaluatedAtUtc",
                table: "MarketPredictions",
                columns: new[] { "TargetTimeUtc", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceAggregates_Interval_BucketStartUtc",
                table: "MarketPriceAggregates",
                columns: new[] { "Interval", "BucketStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceAggregates_MarketItemId_Interval_BucketStartUtc",
                table: "MarketPriceAggregates",
                columns: new[] { "MarketItemId", "Interval", "BucketStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceSnapshots_CollectedAtUtc",
                table: "MarketPriceSnapshots",
                column: "CollectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceSnapshots_CollectionBatchId_MarketItemId",
                table: "MarketPriceSnapshots",
                columns: new[] { "CollectionBatchId", "MarketItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketPriceSnapshots_MarketItemId_CollectedAtUtc",
                table: "MarketPriceSnapshots",
                columns: new[] { "MarketItemId", "CollectedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSignals_MarketItemId_GeneratedAtUtc",
                table: "MarketSignals",
                columns: new[] { "MarketItemId", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSignals_MarketPredictionId",
                table: "MarketSignals",
                column: "MarketPredictionId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketSignals_Status_GeneratedAtUtc",
                table: "MarketSignals",
                columns: new[] { "Status", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketSignals_TradingStrategyId",
                table: "MarketSignals",
                column: "TradingStrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketWatchlists_MarketItemId",
                table: "MarketWatchlists",
                column: "MarketItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PredictionModels_IsActive",
                table: "PredictionModels",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PredictionModels_Name_Version",
                table: "PredictionModels",
                columns: new[] { "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeExecutions_TradePositionId_ExecutedAtUtc",
                table: "TradeExecutions",
                columns: new[] { "TradePositionId", "ExecutedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradePositions_MarketItemId_OpenedAtUtc",
                table: "TradePositions",
                columns: new[] { "MarketItemId", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradePositions_MarketSignalId",
                table: "TradePositions",
                column: "MarketSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_TradePositions_Mode_Status_OpenedAtUtc",
                table: "TradePositions",
                columns: new[] { "Mode", "Status", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradePositions_TradingStrategyId",
                table: "TradePositions",
                column: "TradingStrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategies_IsEnabled",
                table: "TradingStrategies",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategies_Name",
                table: "TradingStrategies",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "MarketPriceAggregates");

            migrationBuilder.DropTable(
                name: "MarketPriceSnapshots");

            migrationBuilder.DropTable(
                name: "MarketWatchlists");

            migrationBuilder.DropTable(
                name: "TradeExecutions");

            migrationBuilder.DropTable(
                name: "MarketCollectionBatches");

            migrationBuilder.DropTable(
                name: "TradePositions");

            migrationBuilder.DropTable(
                name: "MarketSignals");

            migrationBuilder.DropTable(
                name: "MarketPredictions");

            migrationBuilder.DropTable(
                name: "TradingStrategies");

            migrationBuilder.DropTable(
                name: "MarketFeatureSnapshots");

            migrationBuilder.DropTable(
                name: "PredictionModels");

            migrationBuilder.DropTable(
                name: "MarketItems");
        }
    }
}
