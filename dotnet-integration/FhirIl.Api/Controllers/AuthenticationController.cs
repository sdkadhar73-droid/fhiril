using FhirIl.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(IOptions<OAuthOptions> options, IOptions<FhirOptions> fhirOptions) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl, [FromQuery] string? iss, [FromQuery] string? launch)
    {
        if (!options.Value.Mode.Equals("InteractiveOidc", StringComparison.OrdinalIgnoreCase))
            return Conflict(new ProblemDetails { Status = 409, Title = "Interactive SMART/OIDC login is not configured." });
        var fhirBase = fhirOptions.Value.BaseUrl.TrimEnd('/');
        if (iss is not null && !string.Equals(iss.TrimEnd('/'), fhirBase, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Status = 400, Title = "SMART launch issuer does not match the configured FHIR server." });
        if (launch is { Length: > 2000 }) return BadRequest(new ProblemDetails { Status = 400, Title = "SMART launch context is too long." });
        var destination = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        var properties = new AuthenticationProperties { RedirectUri = destination };
        properties.Parameters["aud"] = fhirBase;
        if (!string.IsNullOrWhiteSpace(launch)) properties.Parameters["launch"] = launch;
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout() => SignOut(new AuthenticationProperties { RedirectUri = "/" }, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);
}
