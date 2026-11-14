using System.Security.Claims;
using FhirIl.Api.Configuration;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace FhirIl.Api.Controllers;

[ApiController]
public sealed class LocalOAuthController(IOptions<LocalOAuthOptions> options) : ControllerBase
{
    private readonly LocalOAuthOptions _options = options.Value;

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict authorization request could not be read.");

        if (!string.Equals(request.ClientId, _options.ClientId, StringComparison.Ordinal) ||
            !string.Equals(request.RedirectUri, _options.RedirectUri, StringComparison.Ordinal))
            return BadRequest(new { error = OpenIddictConstants.Errors.InvalidClient, error_description = "The client or redirect URI is not registered." });

        if (!request.GetResponseTypes().Contains(OpenIddictConstants.ResponseTypes.Code, StringComparer.Ordinal))
            return BadRequest(new { error = OpenIddictConstants.Errors.UnsupportedResponseType });

        if (HttpContext.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var action = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/connect/authorize", Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            return Content($"<!doctype html><html><body><h1>FHIRIL sign in</h1><form method=post action=\"{action}\"><label>User <input name=username autocomplete=username></label><br><label>Password <input type=password name=password autocomplete=current-password></label><br><button type=submit>Sign in</button></form></body></html>", "text/html");
        }

        if (!Request.HasFormContentType)
            return BadRequest(new { error = OpenIddictConstants.Errors.InvalidRequest });
        var form = await Request.ReadFormAsync(cancellationToken);
        if (!string.Equals(form["username"], _options.DemoUsername, StringComparison.Ordinal) ||
            !string.Equals(form["password"], _options.DemoPassword, StringComparison.Ordinal))
            return Unauthorized(new { error = OpenIddictConstants.Errors.AccessDenied, error_description = "The supplied credentials are invalid." });

        var scopes = request.GetScopes().Where(scope => _options.Scopes.Contains(scope, StringComparer.Ordinal)).ToArray();
        if (scopes.Length == 0)
            return BadRequest(new { error = OpenIddictConstants.Errors.InvalidScope });

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, _options.DemoUsername);
        identity.SetClaim(OpenIddictConstants.Claims.Name, _options.DemoUsername);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(_options.Audience);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    public async Task<IActionResult> Token(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict token request could not be read.");

        if (request.IsAuthorizationCodeGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            return SignIn(result.Principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (!request.IsClientCredentialsGrantType() ||
            !string.Equals(request.ClientId, _options.MachineClientId, StringComparison.Ordinal) ||
            !string.Equals(request.ClientSecret, _options.MachineClientSecret, StringComparison.Ordinal))
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var scopes = request.GetScopes().Where(scope => _options.Scopes.Contains(scope, StringComparer.Ordinal)).ToArray();
        if (scopes.Length == 0)
            return BadRequest(new { error = OpenIddictConstants.Errors.InvalidScope });

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, _options.MachineClientId);
        identity.SetClaim(OpenIddictConstants.Claims.ClientId, _options.MachineClientId);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(_options.Audience);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
