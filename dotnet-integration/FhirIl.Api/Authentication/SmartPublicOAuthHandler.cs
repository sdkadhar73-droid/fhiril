using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace FhirIl.Api.Authentication;

/// <summary>
/// Public-client SMART OAuth options. Unlike the generic provider options, a public
/// SMART client has no client secret and must not send one to the token endpoint.
/// </summary>
public sealed class SmartPublicOAuthOptions : OAuthOptions
{
    public override void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(ClientId);
        ArgumentException.ThrowIfNullOrEmpty(AuthorizationEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(TokenEndpoint);
        if (!CallbackPath.HasValue) throw new ArgumentException("A callback path is required.", nameof(CallbackPath));
    }
}

/// <summary>
/// Retains ASP.NET OAuth middleware's state, correlation, and PKCE implementation,
/// but exchanges the code as a public client without a client_secret parameter.
/// </summary>
public sealed class SmartPublicOAuthHandler(
    IOptionsMonitor<SmartPublicOAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : OAuthHandler<SmartPublicOAuthOptions>(options, logger, encoder)
{
    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = Options.ClientId,
            ["redirect_uri"] = context.RedirectUri,
            ["code"] = context.Code,
            ["grant_type"] = "authorization_code"
        };
        if (context.Properties.Items.TryGetValue(OAuthConstants.CodeVerifierKey, out var verifier) && !string.IsNullOrWhiteSpace(verifier))
        {
            parameters[OAuthConstants.CodeVerifierKey] = verifier;
            context.Properties.Items.Remove(OAuthConstants.CodeVerifierKey);
        }
        else
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("The PKCE verifier was not present in the protected authorization state."));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Version = Backchannel.DefaultRequestVersion;
        using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
        var content = await response.Content.ReadAsStringAsync(Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return OAuthTokenResponse.Failed(new AuthenticationFailureException($"SMART token endpoint returned HTTP {(int)response.StatusCode}."));
        try
        {
            return OAuthTokenResponse.Success(JsonDocument.Parse(content));
        }
        catch (JsonException)
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("SMART token endpoint returned invalid JSON."));
        }
    }
}
