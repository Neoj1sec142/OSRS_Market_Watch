
using MarketAPI.Data;
using MarketAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Controllers;

[ApiController]
[Route("api/dev/market")]
public sealed class MarketDevController : ControllerBase
{
    private readonly MarketIngestionService _ingestion;
    private readonly MarketDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public MarketDevController(
        MarketIngestionService ingestion,
        MarketDbContext db,
        IWebHostEnvironment environment)
    {
        _ingestion = ingestion;
        _db = db;
        _environment = environment;
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest(
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        var result = await _ingestion.IngestAsync(ct);

        return Ok(result);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        var items = await _db.MarketItems.CountAsync(ct);

        var snapshots = await _db.MarketPriceSnapshots
            .CountAsync(ct);

        var aggregates = await _db.MarketPriceAggregates
            .CountAsync(ct);

        var batches = await _db.MarketCollectionBatches
            .CountAsync(ct);

        var lastBatch = await _db.MarketCollectionBatches
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.StartedAtUtc,
                x.CompletedAtUtc,
                x.ItemsCollected,
                x.ErrorMessage
            })
            .FirstOrDefaultAsync(ct);

        var sample = await _db.MarketPriceSnapshots
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(5)
            .Select(x => new
            {
                ItemId = x.MarketItemId,
                ItemName = x.MarketItem.Name,
                x.InstantBuyPrice,
                x.InstantSellPrice,
                x.CollectedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new
        {
            items,
            snapshots,
            aggregates,
            batches,
            lastBatch,
            sample
        });
    }
}
