using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpeedyCompass.Backend.Services.External;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Backend.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class LocationEngineController : ControllerBase
{
    private readonly RoutingGatewayService _routingService;

    public LocationEngineController(RoutingGatewayService routingService)
    {
        _routingService = routingService;
    }

    [HttpPost("route")]
    public async Task<IActionResult> GetRoute([FromBody] RouteRequestDto req)
    {
        // 1. Fetch the user's tier from the JWT Claim (or fallback to Free)
        string userTier = User.FindFirst("SubscriptionTier")?.Value ?? "Free";

        // 2. Inject it securely into the request so the MAUI app can't forge it
        req.Tier = userTier;

        // 3. Send to orchestrator
        var result = await _routingService.GetRouteAsync(req);
        return result != null ? Ok(result) : BadRequest("Failed to compute route.");
    }

    [HttpPost("traffic")]
    public async Task<IActionResult> GetTraffic([FromBody] RouteRequestDto req)
    {
        var result = await _routingService.GetTrafficWindowAsync(req);
        return result != null ? Ok(result) : BadRequest("Failed to compute traffic.");
    }

    [HttpPost("mapbox-overview")]
    public async Task<IActionResult> GetMapboxOverview([FromBody] List<double[]> points)
    {
        var result = await _routingService.GetMapboxOverviewAsync(points);
        return result != null ? Ok(result) : BadRequest("Failed to compute overview.");
    }
}