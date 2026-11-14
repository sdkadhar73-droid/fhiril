using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FhirIl.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FhirIl.Api.Authentication;

public sealed class OAuthTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<OAuthOptions> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<OAuthTokenProvider> logger) : IAccessTokenProvider
{
    private readonly OAuthOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var mode = _options.Mode;
        if (mode.Equals("Anonymous", StringComparison.OrdinalIgnoreCase)) return null;
        if (mode.Equals("InteractiveOidc", StringComparison.OrdinalIgnoreCase))
        {
            var context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Interactive OAuth requires an HTTP request context.");
            if (context.User.Identity?.IsAuthenticated != true)
                throw new UnauthorizedAccessException("Sign in is required before accessing the FHIR server.");
            var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = auth.Properties;
            var token = properties?.GetTokenValue("access_token");
            if (properties is null || string.IsNullOrWhiteSpace(token))
                throw new UnauthorizedAccessException("The authenticated session has no access token.");
            if (DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), out var expiry) && expiry <= DateTimeOffset.UtcNow.AddSeconds(30))
            {
                var refreshToken = properties.GetTokenValue("refresh_token");
                if (string.IsNullOrWhiteSpace(refreshToken)) throw new UnauthorizedAccessException("The access token expired and no refresh token is available.");
                var endpoint = Required(_options.TokenEndpoint, "OAuth:TokenEndpoint");
                var fields = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = Required(_options.ClientId, "OAuth:ClientId") };
                if (!string.IsNullOrWhiteSpace(_options.ClientSecret)) fields["client_secret"] = _options.ClientSecret;
                using var response = await httpClientFactory.CreateClient("oauth").PostAsync(endpoint, new FormUrlEncodedContent(fields), cancellationToken);
                if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException("OAuth refresh failed; sign in again.");
                var refreshed = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("OAuth refresh response was empty.");
                if (string.IsNullOrWhiteSpace(refreshed.AccessToken) || refreshed.ExpiresIn <= 0)
                    throw new InvalidOperationException("OAuth refresh response is missing a usable access token or expiry.");
                token = refreshed.AccessToken;
                properties.UpdateTokenValue("access_token", refreshed.AccessToken);
                properties.UpdateTokenValue("expires_at", DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn).ToString("o"));
                if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken)) properties.UpdateTokenValue("refresh_token", refreshed.RefreshToken);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, auth.Principal!, properties);
            }
            return token;
        }

        if (!mode.Equals("ClientCredentials", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("SmartBackendServices", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported OAuth mode '{mode}'.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return _cachedToken;
            var endpoint = Required(_options.TokenEndpoint, "OAuth:TokenEndpoint");
            var clientId = Required(_options.ClientId, "OAuth:ClientId");
            using var content = new FormUrlEncodedContent(await BuildTokenFormAsync(mode, endpoint, clientId, cancellationToken));
            using var response = await httpClientFactory.CreateClient("oauth").PostAsync(endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OAuth token endpoint returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw new HttpRequestException("OAuth token acquisition failed.", null, response.StatusCode);
            }
            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("OAuth token endpoint returned an empty response.");
            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || tokenResponse.ExpiresIn <= 0)
                throw new InvalidOperationException("OAuth token response is missing a usable access token or expiry.");
            _cachedToken = tokenResponse.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            return _cachedToken;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> BuildTokenFormAsync(string mode, string endpoint, string clientId, CancellationToken cancellationToken)
    {
        var scope = string.Join(' ', _options.Scopes);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope
        };
        if (mode.Equals("ClientCredentials", StringComparison.OrdinalIgnoreCase))
        {
            form["client_id"] = clientId;
            var secret = Required(_options.ClientSecret, "OAuth:ClientSecret");
            form["client_secret"] = secret;
            return form;
        }

        var keyPath = Required(_options.SigningKeyPath, "OAuth:SigningKeyPath");
        var fullPath = Path.GetFullPath(keyPath);
        if (!File.Exists(fullPath)) throw new InvalidOperationException("Configured OAuth signing-key file was not found.");
        var pem = await File.ReadAllTextAsync(fullPath, cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = DateTimeOffset.UtcNow;
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = _options.SigningKeyId }, SecurityAlgorithms.RsaSha256);
        var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", clientId), new System.Security.Claims.Claim("jti", Guid.NewGuid().ToString("N"))]),
            Audience = endpoint,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = credentials,
            TokenType = "JWT"
        });
        form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        form["client_assertion"] = assertion;
        form["client_id"] = clientId;
        return form;
    }

    private static string Required(string? value, string setting) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Required setting {setting} is missing.") : value;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);
}
