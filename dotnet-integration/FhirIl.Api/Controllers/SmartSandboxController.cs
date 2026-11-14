using System.Security.Claims;
using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using FhirIl.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Controllers;

[ApiController]
[Route("smart")]
public sealed class SmartSandboxController(
    IOptions<SmartSandboxOptions> options,
    SmartSandboxDiscovery discovery,
    SmartSandboxFhirClient fhir,
    ISmartSandboxSessionStore sessions) : ControllerBase
{
    private const string CookieScheme = "SmartSandboxCookie";
    private const string OAuthScheme = "SmartSandbox";

    [AllowAnonymous]
    [HttpGet]
    [Produces("text/html")]
    public ContentResult Home([FromQuery] string? error = null)
    {
        var errorMessage = error == "authorization_failed" ? "Authorization did not complete. Check that the sandbox returned a valid patient context and requested scope." : "";
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>FHIRIL SMART Sandbox</title></head><body><h1>FHIRIL SMART Sandbox</h1><p>Provider Standalone Launch against the configured SMART Health IT FHIR R4 sandbox.</p><p>{errorMessage}</p><p><a href=\"/smart/status\">Check sandbox discovery</a></p><p><a href=\"/smart/launch\">Start read-only authorization</a></p><p>Requested scopes: {System.Net.WebUtility.HtmlEncode(string.Join(' ', options.Value.Scopes))}</p></body></html>";
        return Content(body, "text/html");
    }

    [AllowAnonymous]
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return Conflict(new { status = "disabled" });
        var result = await discovery.GetAsync(cancellationToken);
        return Ok(new
        {
            status = "ready",
            result.Issuer,
            result.AuthorizationEndpoint,
            result.TokenEndpoint,
            result.FhirVersion,
            result.GrantTypes,
            result.PkceMethods,
            result.Capabilities,
            configuredScopes = options.Value.Scopes,
            callbackUri = options.Value.RedirectUri
        });
    }

    [AllowAnonymous]
    [HttpGet("launch")]
    public async Task<IActionResult> Launch(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return Conflict(new ProblemDetails { Status = 409, Title = "SMART Sandbox integration is disabled." });
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.RedirectUri))
            return Conflict(new ProblemDetails { Status = 409, Title = "SMART Sandbox client ID and redirect URI must be configured." });

        var expectedRedirect = new Uri(settings.RedirectUri, UriKind.Absolute);
        var requestOrigin = $"{Request.Scheme}://{Request.Host}";
        if (!string.Equals(expectedRedirect.AbsolutePath, settings.CallbackPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(expectedRedirect.Query) || !string.IsNullOrEmpty(expectedRedirect.Fragment) ||
            !string.Equals(requestOrigin, expectedRedirect.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Request.Scheme, expectedRedirect.Scheme, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Status = 400, Title = "The current request origin does not match the configured SMART callback URL." });

        _ = await discovery.GetAsync(cancellationToken);
        var sessionId = Guid.NewGuid().ToString("N");
        var properties = new AuthenticationProperties { RedirectUri = "/smart/session", ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        properties.Items["smart_session_id"] = sessionId;
        return Challenge(properties, OAuthScheme);
    }

    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<IActionResult> Session()
    {
        var auth = await HttpContext.AuthenticateAsync(CookieScheme);
        if (!auth.Succeeded || auth.Principal is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "No active SMART Sandbox session." });
        var id = auth.Principal.FindFirstValue("smart_session_id");
        var patientId = auth.Principal.FindFirstValue("smart_patient_id");
        if (id is null || patientId is null || !sessions.TryGet(id, out var session))
        {
            await HttpContext.SignOutAsync(CookieScheme);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "The SMART Sandbox session has expired or is incomplete." });
        }
        return Ok(new
        {
            status = "authenticated",
            issuer = session.Issuer,
            patientId = session.PatientId,
            scopes = session.Scope,
            expiresAt = session.ExpiresAt
        });
    }

    [AllowAnonymous]
    [HttpGet("patient")]
    public Task<IActionResult> ReadAuthorizedPatient(CancellationToken cancellationToken) => ReadPatientCoreAsync(null, cancellationToken);

    [AllowAnonymous]
    [HttpGet("patient/{id}")]
    public Task<IActionResult> ReadPatientById(string id, CancellationToken cancellationToken) => ReadPatientCoreAsync(id, cancellationToken);

    private async Task<IActionResult> ReadPatientCoreAsync(string? requestedId, CancellationToken cancellationToken)
    {
        var auth = await HttpContext.AuthenticateAsync(CookieScheme);
        if (!auth.Succeeded || auth.Principal is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "SMART authorization is required." });
        var sessionId = auth.Principal.FindFirstValue("smart_session_id");
        if (sessionId is null || !sessions.TryGet(sessionId, out var session))
        {
            await HttpContext.SignOutAsync(CookieScheme);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "The SMART Sandbox session has expired." });
        }
        var patient = await fhir.ReadAuthorizedPatientAsync(session, requestedId, cancellationToken);
        return Ok(new { resourceType = patient.TypeName, id = patient.Id });
    }
}
