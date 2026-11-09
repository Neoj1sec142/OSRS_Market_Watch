
using MarketAPI.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly MarketDbContext _db;

    public HealthController(MarketDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken ct)
    {
        var databaseConnected =
            await _db.Database.CanConnectAsync(ct);

        if (!databaseConnected)
        {
            return StatusCode(503, new
            {
                status = "Unhealthy",
                database = "Disconnected"
            });
        }

        return Ok(new
        {
            status = "Healthy",
            database = "Connected",
            timestampUtc = DateTime.UtcNow
        });
    }
}
