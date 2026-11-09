using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Services;

public sealed class MarketAnalyticsService
{
    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly ILogger<MarketAnalyticsService> _logger;

    // Initial quality thresholds based on the observed OSRS data.
    // These are heuristics, not guarantees that an hour is complete.
    private const int MinimumItemsPerHour = 1_500;
    private const int MinimumTwoSidedItemsPerHour = 500;
    private static readonly TimeSpan FreshnessLimit = TimeSpan.FromHours(2);

    public MarketAnalyticsService(
        IDbContextFactory<MarketDbContext> factory,
        ILogger<MarketAnalyticsService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    
    public async Task<MarketOpportunityResponse> GetOpportunitiesAsync(
        MarketOpportunityQuery query,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // ---------------------------------------------------------
        // 1. Normalize query parameters
        // ---------------------------------------------------------

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var maxQuoteAge = Math.Clamp(
            query.MaxQuoteAgeMinutes, 1, 1440);

        var budgetGp = Math.Max(0L, query.BudgetGp);
        var minVolume = Math.Max(0L, query.MinHourlyVolume);
        var minProfit = Math.Max(0L, query.MinTotalProfitGp);
        var minRoi = Math.Max(0m, query.MinRoiPercent);

        var participationRate = Math.Clamp(
            query.VolumeParticipationRate, 0.001m, 1m);

        await using var db =
            await _factory.CreateDbContextAsync(ct);

        // ---------------------------------------------------------
        // 2. Retrieve the latest price snapshot for each item
        //
        // Snapshots are deduplicated during ingestion, so the
        // latest snapshot for an item may belong to an older batch.
        // ---------------------------------------------------------

        var latestIds = db.MarketPriceSnapshots
            .AsNoTracking()
            .GroupBy(x => x.MarketItemId)
            .Select(g => g.Max(x => x.Id));

        var prices = await db.MarketPriceSnapshots
            .AsNoTracking()
            .Where(s => latestIds.Contains(s.Id))
            .Where(s => s.MarketItem.IsActive)
            .Select(s => new
            {
                s.MarketItemId,
                s.InstantBuyPrice,
                s.InstantSellPrice,
                s.InstantBuyTimeUtc,
                s.InstantSellTimeUtc,

                Name = s.MarketItem.Name,
                BuyLimit = s.MarketItem.BuyLimit,
                IsTaxExempt = s.MarketItem.IsTaxExempt
            })
            .ToListAsync(ct);

        // ---------------------------------------------------------
        // 3. Calculate completed hourly boundary
        //
        // Example:
        // Current UTC time: 19:57
        // completedBeforeUtc: 19:00
        //
        // Only buckets starting before this time are eligible.
        // ---------------------------------------------------------

        var completedBeforeUtc = new DateTime(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            0,
            0,
            DateTimeKind.Utc);

        // ---------------------------------------------------------
        // 4. Identify a reference hourly bucket with reasonable
        //    market coverage.
        //
        // This is used for response-level market data status.
        // It is NOT used to restrict every item to one hour.
        // ---------------------------------------------------------

        var hourlyBuckets = await db.MarketPriceAggregates
            .AsNoTracking()
            .Where(a =>
                a.Interval == AggregateInterval.OneHour &&
                a.BucketStartUtc < completedBeforeUtc)
            .GroupBy(a => a.BucketStartUtc)
            .Select(g => new
            {
                BucketStartUtc = g.Key,
                ItemCount = g.Count(),
                TwoSidedItemCount = g.Count(x =>
                    x.InstantBuyVolume > 0 &&
                    x.InstantSellVolume > 0)
            })
            .OrderByDescending(x => x.BucketStartUtc)
            .Take(24)
            .ToListAsync(ct);

        // Initial quality thresholds based on the data we've seen.
        // These are research heuristics, not source guarantees.
        var selectedBucket = hourlyBuckets.FirstOrDefault(x =>
            x.ItemCount >= 1_500 &&
            x.TwoSidedItemCount >= 500);

        if (selectedBucket == null)
        {
            _logger.LogWarning(
                "Analytics: No hourly bucket meets quality thresholds.");

            return EmptyResponse(
                now,
                budgetGp,
                page,
                pageSize,
                MarketDataStatus.Unavailable,
                "No hourly buckets meet the minimum data-quality requirements.");
        }

        var bucketUtc = DateTime.SpecifyKind(
            selectedBucket.BucketStartUtc,
            DateTimeKind.Utc);

        // Hourly bucket covers [BucketStart, BucketStart + 1 hour).
        var completedAtUtc = bucketUtc.AddHours(1);
        var bucketAge = now - completedAtUtc;

        var dataStatus = bucketAge > TimeSpan.FromHours(2)
            ? MarketDataStatus.Stale
            : MarketDataStatus.Fresh;

        _logger.LogInformation(
            "Analytics reference bucket: {Bucket}, " +
            "Items={Items}, TwoSided={TwoSided}, " +
            "AgeSinceEndMinutes={Age:F1}, Status={Status}",
            bucketUtc,
            selectedBucket.ItemCount,
            selectedBucket.TwoSidedItemCount,
            bucketAge.TotalMinutes,
            dataStatus);

        // ---------------------------------------------------------
        // 5. Retrieve per-item hourly liquidity observations
        //
        // IMPORTANT:
        // We no longer require every item to have activity in
        // the same global hourly bucket.
        //
        // Each item gets its most recent qualifying two-sided
        // hourly volume observation from the last 12 hours.
        // ---------------------------------------------------------

        const int volumeLookbackHours = 12;

        var volumeCutoffUtc = now.AddHours(-volumeLookbackHours);

        var volumeRows = await db.MarketPriceAggregates
            .AsNoTracking()
            .Where(a =>
                a.Interval == AggregateInterval.OneHour &&
                a.BucketStartUtc >= volumeCutoffUtc &&
                a.BucketStartUtc < completedBeforeUtc &&
                a.InstantBuyVolume > 0 &&
                a.InstantSellVolume > 0)
            .Select(a => new
            {
                a.MarketItemId,
                a.InstantBuyVolume,
                a.InstantSellVolume,
                a.BucketStartUtc
            })
            .ToListAsync(ct);

        var volumes = volumeRows
            .GroupBy(x => x.MarketItemId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(
                    x => x.BucketStartUtc).First()
            );

        _logger.LogInformation(
            "Analytics hourly volume: Rows={Rows}, " +
            "DistinctItems={Items}, LookbackHours={Hours}",
            volumeRows.Count,
            volumes.Count,
            volumeLookbackHours);

        // ---------------------------------------------------------
        // 6. Initialize candidates and diagnostic counters
        //
        // Each item is assigned to its FIRST rejection reason.
        // This makes the counters easy to reconcile.
        // ---------------------------------------------------------

        var candidates = new List<MarketOpportunityDto>();

        int examined = 0;
        int searchFiltered = 0;
        int missingPrices = 0;
        int noSpread = 0;
        int staleQuotes = 0;
        int missingVolume = 0;
        int insufficientVolume = 0;
        int missingBuyLimits = 0;
        int unprofitable = 0;
        int belowRoi = 0;
        int insufficientCapacity = 0;
        int belowProfit = 0;

        // ---------------------------------------------------------
        // 7. Evaluate each item
        // ---------------------------------------------------------

        foreach (var price in prices)
        {
            ct.ThrowIfCancellationRequested();
            examined++;

            // Optional item-name filter
            if (!string.IsNullOrWhiteSpace(query.Search) &&
                !price.Name.Contains(
                    query.Search,
                    StringComparison.OrdinalIgnoreCase))
            {
                searchFiltered++;
                continue;
            }

            // Validate price and timestamp availability
            if (price.InstantBuyPrice is not > 0 ||
                price.InstantSellPrice is not > 0 ||
                price.InstantBuyTimeUtc == null ||
                price.InstantSellTimeUtc == null)
            {
                missingPrices++;
                continue;
            }

            // Low observed price = hypothetical entry.
            // High observed price = hypothetical exit.
            long entryPrice = price.InstantSellPrice.Value;
            long exitPrice = price.InstantBuyPrice.Value;

            if (exitPrice <= entryPrice)
            {
                noSpread++;
                continue;
            }

            // SQLite may materialize timestamps with
            // DateTimeKind.Unspecified.
            var buyTime = DateTime.SpecifyKind(
                price.InstantBuyTimeUtc.Value,
                DateTimeKind.Utc);

            var sellTime = DateTime.SpecifyKind(
                price.InstantSellTimeUtc.Value,
                DateTimeKind.Utc);

            double buyAge = (now - buyTime).TotalMinutes;
            double sellAge = (now - sellTime).TotalMinutes;

            // Both sides must be within the requested age window.
            if (buyAge < 0 ||
                sellAge < 0 ||
                buyAge > maxQuoteAge ||
                sellAge > maxQuoteAge)
            {
                staleQuotes++;
                continue;
            }

            // Look up the most recent qualifying hourly volume
            // observation for THIS item.
            if (!volumes.TryGetValue(
                price.MarketItemId, out var volume))
            {
                missingVolume++;
                continue;
            }

            // Conservative two-sided volume estimate.
            long conservativeVolume = Math.Min(
                volume.InstantBuyVolume,
                volume.InstantSellVolume);

            if (conservativeVolume < minVolume)
            {
                insufficientVolume++;
                continue;
            }

            // A usable buy limit is required for quantity estimates.
            if (price.BuyLimit is not > 0)
            {
                missingBuyLimits++;
                continue;
            }

            // -----------------------------------------------------
            // 8. Calculate after-tax profit
            // -----------------------------------------------------

            long tax = GrandExchangeTaxService.Calculate(
                exitPrice,
                price.IsTaxExempt);

            long grossSpread = exitPrice - entryPrice;
            long netProfit = grossSpread - tax;

            if (netProfit <= 0)
            {
                unprofitable++;
                continue;
            }

            decimal roi = GrandExchangeTaxService
                .CalculateRoiPercent(entryPrice, netProfit);

            if (roi < minRoi)
            {
                belowRoi++;
                continue;
            }

            // -----------------------------------------------------
            // 9. Calculate estimated quantity and total profit
            //
            // Quantity is constrained by:
            // - Available GP
            // - GE buy limit
            // - Conservative fraction of hourly volume
            // -----------------------------------------------------

            long affordableQuantity = budgetGp / entryPrice;

            long volumeCapacity = (long)Math.Floor(
                conservativeVolume * participationRate);

            long suggestedQuantity = Math.Min(
                affordableQuantity,
                Math.Min(
                    (long)price.BuyLimit.Value,
                    volumeCapacity));

            suggestedQuantity = Math.Min(
                suggestedQuantity, int.MaxValue);

            if (suggestedQuantity <= 0)
            {
                insufficientCapacity++;
                continue;
            }

            long totalProfit = checked(
                netProfit * suggestedQuantity);

            if (totalProfit < minProfit)
            {
                belowProfit++;
                continue;
            }

            long requiredCapital = checked(
                entryPrice * suggestedQuantity);

            decimal spreadPercent =
                (decimal)grossSpread / entryPrice * 100m;

            // -----------------------------------------------------
            // 10. Build the opportunity DTO
            // -----------------------------------------------------

            candidates.Add(new MarketOpportunityDto
            {
                ItemId = price.MarketItemId,
                Name = price.Name,
                BuyLimit = price.BuyLimit,

                ObservedInstantBuyPriceGp = exitPrice,
                ObservedInstantSellPriceGp = entryPrice,

                EstimatedEntryPriceGp = entryPrice,
                EstimatedExitPriceGp = exitPrice,

                GrossSpreadGp = grossSpread,
                GrossSpreadPercent = spreadPercent,

                TaxPerItemGp = tax,
                NetProfitPerItemGp = netProfit,
                RoiPercent = roi,

                HourlyInstantBuyVolume =
                    volume.InstantBuyVolume,

                HourlyInstantSellVolume =
                    volume.InstantSellVolume,

                ConservativeHourlyVolume =
                    conservativeVolume,

                SuggestedQuantity = (int)suggestedQuantity,

                EstimatedCapitalRequiredGp =
                    requiredCapital,

                EstimatedTotalProfitGp =
                    totalProfit,

                InstantBuyTimeUtc = buyTime,
                InstantSellTimeUtc = sellTime,

                // This is the actual volume bucket used for
                // this specific item, not the reference bucket.
                VolumeBucketStartUtc = DateTime.SpecifyKind(
                    volume.BucketStartUtc,
                    DateTimeKind.Utc),

                InstantBuyAgeMinutes = buyAge,
                InstantSellAgeMinutes = sellAge,

                LiquidityParticipationPercent =
                    (decimal)suggestedQuantity
                    / conservativeVolume * 100m
            });
        }

        // ---------------------------------------------------------
        // 11. Log complete diagnostic funnel
        // ---------------------------------------------------------

        _logger.LogInformation(
            "Analytics filter funnel: PriceRows={PriceRows}, " +
            "SearchFiltered={SearchFiltered}, " +
            "MissingPrices={MissingPrices}, " +
            "NoSpread={NoSpread}, " +
            "StaleQuotes={StaleQuotes}, " +
            "MissingVolume={MissingVolume}, " +
            "LowVolume={LowVolume}, " +
            "NoBuyLimit={NoBuyLimit}, " +
            "Unprofitable={Unprofitable}, " +
            "BelowRoi={BelowRoi}, " +
            "NoCapacity={NoCapacity}, " +
            "BelowProfit={BelowProfit}, " +
            "Candidates={Candidates}",
            examined,
            searchFiltered,
            missingPrices,
            noSpread,
            staleQuotes,
            missingVolume,
            insufficientVolume,
            missingBuyLimits,
            unprofitable,
            belowRoi,
            insufficientCapacity,
            belowProfit,
            candidates.Count);

        // ---------------------------------------------------------
        // 12. Rank candidates
        // ---------------------------------------------------------

        var ordered = candidates
            .OrderByDescending(
                x => x.EstimatedTotalProfitGp)
            .ThenByDescending(x => x.RoiPercent)
            .ThenByDescending(
                x => x.ConservativeHourlyVolume)
            .ToList();

        var totalCandidates = ordered.Count;

        var result = ordered
            .Skip((int)Math.Min(
                (long)(page - 1) * pageSize,
                int.MaxValue))
            .Take(pageSize)
            .ToList();

        _logger.LogInformation(
            "Market analytics completed: " +
            "Items={Items}, Candidates={Candidates}, " +
            "Budget={Budget} GP, Status={Status}",
            prices.Count,
            totalCandidates,
            budgetGp,
            dataStatus);

        // ---------------------------------------------------------
        // 13. Return response for API / Angular frontend
        // ---------------------------------------------------------

        return new MarketOpportunityResponse
        {
            AnalyzedAtUtc = now,

            DataStatus = dataStatus,

            // Reference quality bucket, not necessarily the same
            // as every individual item's volume bucket.
            LatestHourlyBucketUtc = bucketUtc,

            DiagnosticMessage = dataStatus == MarketDataStatus.Stale
                ? "Hourly volume is historical. " +
                "Opportunities are research-only and may not " +
                "reflect current trading conditions."
                : null,

            BudgetGp = budgetGp,
            TotalCandidates = totalCandidates,
            Page = page,
            PageSize = pageSize,

            TotalPages = (int)Math.Ceiling(
                (double)totalCandidates / pageSize),

            Opportunities = result
        };
    }


    private static MarketOpportunityResponse EmptyResponse(
        DateTime now, long budgetGp, int page, int pageSize,
        MarketDataStatus status, string message)
    {
        return new MarketOpportunityResponse
        {
            AnalyzedAtUtc = now,
            BudgetGp = budgetGp,
            DataStatus = status,
            DiagnosticMessage = message,
            Page = page,
            PageSize = pageSize,
            TotalCandidates = 0,
            TotalPages = 0,
            Opportunities = []
        };
    }
}
