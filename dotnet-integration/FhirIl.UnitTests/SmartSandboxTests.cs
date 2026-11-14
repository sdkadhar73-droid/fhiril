using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using FhirIl.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;

namespace FhirIl.UnitTests;

public sealed class SmartSandboxTests
{
    private const string Issuer = "https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/fhir";
    private const string Authorization = "https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/auth/authorize";
    private const string Token = "https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/auth/token";

    [Fact]
    public void IssuerAndOAuthEndpointsMustMatchApprovedSandboxConfiguration()
    {
        var issuer = SmartSandboxIssuer.Validate(Issuer);
        Assert.Equal(new Uri(Authorization), SmartSandboxIssuer.ValidateEndpoint(Authorization, issuer, "/auth/authorize"));
        Assert.Equal(new Uri(Token), SmartSandboxIssuer.ValidateEndpoint(Token, issuer, "/auth/token"));
        Assert.Throws<ArgumentException>(() => SmartSandboxIssuer.Validate("https://r4.smarthealthit.org/fhir"));
        Assert.Throws<ArgumentException>(() => SmartSandboxIssuer.Validate("http://launch.smarthealthit.org/v/r4/fhir"));
        Assert.Throws<ArgumentException>(() => SmartSandboxIssuer.ValidateEndpoint("https://evil.example/auth/token", issuer, "/auth/token"));
        Assert.Throws<ArgumentException>(() => SmartSandboxIssuer.ValidateEndpoint("https://launch.smarthealthit.org/v/r4/auth/token", issuer, "/auth/token"));
    }

    [Fact]
    public async Task DiscoveryRequiresR4AuthorizationCodeS256AndMatchingEndpoints()
    {
        var requestPaths = new List<string>();
        var service = new SmartSandboxDiscovery(new SingleClientFactory(new DelegateHandler(request =>
        {
            requestPaths.Add(request.RequestUri!.AbsolutePath);
            var body = request.RequestUri.AbsolutePath.EndsWith("smart-configuration", StringComparison.Ordinal)
                ? $$"""{"issuer":"{{Issuer}}","authorization_endpoint":"{{Authorization}}","token_endpoint":"{{Token}}","grant_types_supported":["authorization_code","client_credentials"],"code_challenge_methods_supported":["S256"],"scopes_supported":["launch/patient","patient/*.*"],"capabilities":["launch-standalone","client-public","context-standalone-patient","permission-patient","permission-v2"]}"""
                : "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.0\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), Options.Create(Config()));

        var result = await service.GetAsync(CancellationToken.None);
        Assert.Equal(Issuer, result.Issuer);
        Assert.Equal("4.0.0", result.FhirVersion);
        Assert.Contains("S256", result.PkceMethods);
        Assert.Contains("launch-standalone", result.Capabilities);
        Assert.Equal(2, requestPaths.Count);
    }

    [Fact]
    public async Task DiscoveryRejectsAuthorizationEndpointThatWasChanged()
    {
        var service = new SmartSandboxDiscovery(new SingleClientFactory(new DelegateHandler(request =>
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("smart-configuration", StringComparison.Ordinal)
                ? $$"""{"issuer":"{{Issuer}}","authorization_endpoint":"https://evil.example/authorize","token_endpoint":"{{Token}}","grant_types_supported":["authorization_code"],"code_challenge_methods_supported":["S256"]}"""
                : "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.0\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), Options.Create(Config()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DiscoveryRejectsNonR4CapabilityStatement()
    {
        var service = new SmartSandboxDiscovery(new SingleClientFactory(new DelegateHandler(request =>
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("smart-configuration", StringComparison.Ordinal)
                ? $$"""{"issuer":"{{Issuer}}","authorization_endpoint":"{{Authorization}}","token_endpoint":"{{Token}}","grant_types_supported":["authorization_code"],"code_challenge_methods_supported":["S256"],"scopes_supported":["launch/patient","patient/*.*"],"capabilities":["launch-standalone","client-public","context-standalone-patient","permission-patient","permission-v2"]}"""
                : "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"5.0.0\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), Options.Create(Config()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SandboxBearerHandlerBlocksLocalHapiAndOnlyAttachesTokenToSandboxUri()
    {
        var sent = new List<(Uri? Uri, string? Scheme, string? Token)>();
        var sandbox = SmartSandboxIssuer.Validate(Issuer);
        var handler = new SandboxBearerHandler(sandbox, "sandbox-only-test-token")
        {
            InnerHandler = new DelegateHandler(request =>
            {
                sent.Add((request.RequestUri, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            })
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var okRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(sandbox, "Patient/synthetic-1"));
        using var okResponse = await invoker.SendAsync(okRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
        Assert.Equal("Bearer", sent.Single().Scheme);
        Assert.Equal("sandbox-only-test-token", sent.Single().Token);
        Assert.StartsWith(sandbox.AbsoluteUri, sent.Single().Uri!.AbsoluteUri, StringComparison.Ordinal);

        using var localRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/fhir/Patient/1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(localRequest, CancellationToken.None));
        Assert.Single(sent);
    }

    [Fact]
    public async Task PatientContextRejectsDifferentPatientAndExpiredSessionBeforeNetworkAccess()
    {
        var client = new SmartSandboxFhirClient(Options.Create(Config()));
        var active = new SmartSandboxSession("token", "synthetic-1", Issuer, "patient/Patient.r", DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ReadAuthorizedPatientAsync(active, "synthetic-2", CancellationToken.None));
        var expired = active with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ReadAuthorizedPatientAsync(expired, null, CancellationToken.None));
        var noScope = active with { Scope = "patient/Observation.r" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ReadAuthorizedPatientAsync(noScope, null, CancellationToken.None));
    }

    [Fact]
    public async Task AspNetOAuthMiddlewareUsesS256StateValidationAndOriginalVerifierForExchange()
    {
        string? postedForm = null;
        var tokenHandler = new DelegateHandler(async request =>
        {
            postedForm = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"synthetic-token\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"patient/Patient.read\",\"patient\":\"synthetic-42\"}", Encoding.UTF8, "application/json")
            };
        });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options => options.DefaultScheme = "test-cookie")
            .AddCookie("test-cookie")
            .AddOAuth<SmartPublicOAuthOptions, SmartPublicOAuthHandler>("sandbox", options =>
            {
                options.SignInScheme = "test-cookie";
                options.ClientId = "fhiril-smart-demo";
                options.AuthorizationEndpoint = "https://auth.example.test/authorize";
                options.TokenEndpoint = "https://auth.example.test/token";
                options.CallbackPath = "/signin-sandbox";
                options.UsePkce = true;
                options.Scope.Add("patient/Patient.read");
                options.Backchannel = new HttpClient(tokenHandler);
                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = context =>
                    {
                        ((System.Security.Claims.ClaimsIdentity)context.Principal!.Identity!).AddClaim(new System.Security.Claims.Claim("patient", "synthetic-42"));
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return Task.CompletedTask;
                    }
                };
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/start", (HttpContext context) => context.ChallengeAsync("sandbox", new AuthenticationProperties { RedirectUri = "/done" }));
        app.MapGet("/done", async (HttpContext context) =>
        {
            var auth = await context.AuthenticateAsync("test-cookie");
            return auth.Succeeded ? Results.Ok(auth.Principal!.FindFirst("patient")!.Value) : Results.Unauthorized();
        });
        await app.StartAsync();
        var client = app.GetTestClient();

        var challengeResponse = await client.GetAsync("/start");
        Assert.Equal(HttpStatusCode.Redirect, challengeResponse.StatusCode);
        var location = challengeResponse.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.NotEmpty(query["code_challenge"].ToString());
        var state = query["state"].ToString();
        var oauthOptions = app.Services.GetRequiredService<IOptionsMonitor<SmartPublicOAuthOptions>>().Get("sandbox");
        var protectedState = oauthOptions.StateDataFormat.Unprotect(state);
        Assert.NotNull(protectedState);
        var verifier = protectedState!.Items[OAuthConstants.CodeVerifierKey];
        Assert.False(string.IsNullOrWhiteSpace(verifier));
        var expectedChallenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier!)));
        Assert.Equal(expectedChallenge, query["code_challenge"].ToString());

        var cookie = challengeResponse.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]).ToArray();
        using var callback = new HttpRequestMessage(HttpMethod.Get, "/signin-sandbox?code=synthetic-code&state=" + Uri.EscapeDataString(state));
        callback.Headers.Add("Cookie", cookie);
        var callbackResponse = await client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Contains("code_verifier=", postedForm);
        Assert.Contains("code=synthetic-code", postedForm);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fsignin-sandbox", postedForm);
        Assert.DoesNotContain("client_secret", postedForm, StringComparison.OrdinalIgnoreCase);

        using var badCallback = new HttpRequestMessage(HttpMethod.Get, "/signin-sandbox?code=synthetic-code&state=wrong-state");
        badCallback.Headers.Add("Cookie", cookie);
        var badResponse = await client.SendAsync(badCallback);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
        await app.DisposeAsync();
    }

    private static SmartSandboxOptions Config() => new()
    {
        Enabled = true,
        Issuer = Issuer,
        AuthorizationEndpoint = Authorization,
        TokenEndpoint = Token,
        ClientId = "fhiril-smart-demo",
        RedirectUri = "http://localhost:5265/smart/callback",
        CallbackPath = "/smart/callback",
        Scopes = ["launch/patient", "patient/Patient.r"]
    };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _callback = request => Task.FromResult(callback(request));

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : this(request => new HttpResponseMessage(HttpStatusCode.InternalServerError))
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _callback(request);
    }
}
