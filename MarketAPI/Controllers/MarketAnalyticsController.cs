
using MarketAPI.Models;
using MarketAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace MarketAPI.Controllers;

[ApiController]
[Route("api/market/analytics")]
public sealed class MarketAnalyticsController : ControllerBase
{
    private readonly MarketAnalyticsService _analytics;

    public MarketAnalyticsController(
        MarketAnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("opportunities")]
    public async Task<ActionResult<MarketOpportunityResponse>>
        GetOpportunities(
            [FromQuery] MarketOpportunityQuery query,
            CancellationToken ct)
    {
        if (query.BudgetGp <= 0)
            return BadRequest(
                "BudgetGp must be greater than zero.");

        if (query.MinHourlyVolume < 0 ||
            query.MinTotalProfitGp < 0 ||
            query.MinRoiPercent < 0)
            return BadRequest(
                "Minimum filters cannot be negative.");

        if (query.VolumeParticipationRate <= 0 ||
            query.VolumeParticipationRate > 1)
            return BadRequest(
                "VolumeParticipationRate must be between 0 and 1.");

        if (query.Page < 1 || query.PageSize < 1 ||
            query.PageSize > 100)
            return BadRequest(
                "Page must be positive and PageSize must be 1-100.");

        var result = await _analytics
            .GetOpportunitiesAsync(query, ct);

        return Ok(result);
    }
}
