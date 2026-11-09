
namespace MarketAPI.Services;

public static class GrandExchangeTaxService
{
    private const long MaximumTaxGp = 5_000_000;

    public static long Calculate(
        long salePriceGp,
        bool isTaxExempt = false)
    {
        if (salePriceGp <= 0 || isTaxExempt)
            return 0;

        // 2% tax, truncated to whole GP.
        // Divide before multiplying to avoid overflow.
        long tax = (salePriceGp / 50)
            + ((salePriceGp % 50) * 2 / 100);

        return Math.Min(tax, MaximumTaxGp);
    }

    public static long CalculateNetProfit(
        long entryPriceGp,
        long exitPriceGp,
        bool isTaxExempt = false)
    {
        if (entryPriceGp <= 0 || exitPriceGp <= 0)
            return 0;

        long tax = Calculate(exitPriceGp, isTaxExempt);

        return exitPriceGp - entryPriceGp - tax;
    }

    public static decimal CalculateRoiPercent(
        long entryPriceGp,
        long netProfitGp)
    {
        if (entryPriceGp <= 0)
            return 0;

        return (decimal)netProfitGp
            / entryPriceGp * 100m;
    }
}
