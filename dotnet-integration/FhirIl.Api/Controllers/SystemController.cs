using FhirIl.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirIl.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class SystemController(FhirServerDiscovery discovery) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "healthy" });

    [AllowAnonymous]
    [HttpGet("health/fhir")]
    public async Task<IActionResult> FhirHealth(CancellationToken ct)
    {
        var result = await discovery.GetAsync(ct);
        var status = result.FhirVersion == "4.0.1" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        return StatusCode(status, new { result.Connectivity, result.MetadataStatus, result.FhirVersion, result.ImplementationDescription, result.SmartStatus });
    }

    [AllowAnonymous]
    [HttpGet("fhir/capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken ct)
    {
        var result = await discovery.GetAsync(ct);
        return Ok(result);
    }
}
