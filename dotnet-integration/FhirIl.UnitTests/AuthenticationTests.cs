using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Task = System.Threading.Tasks.Task;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using FhirIl.Api.Controllers;
using FhirIl.Api.Models;
using FhirIl.Api.Services;
using FhirIl.Api.Infrastructure;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirIl.UnitTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task AnonymousModeReturnsNoBearerToken()
    {
        var provider = Provider(new OAuthOptions { Mode = "Anonymous" }, _ => throw new Xunit.Sdk.XunitException("Anonymous mode must not call the token endpoint."));
        Assert.Null(await provider.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedModeDoesNotDowngradeWhenConfigurationIsMissing()
    {
        var provider = Provider(new OAuthOptions { Mode = "ClientCredentials", ClientId = "test-client" }, _ => throw new Xunit.Sdk.XunitException("Token endpoint must not be called."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClientCredentialsTokenIsAcquiredAndCached()
    {
        var requests = 0;
        var provider = Provider(new OAuthOptions
        {
            Mode = "ClientCredentials", ClientId = "synthetic-client", ClientSecret = "test-only", TokenEndpoint = "https://auth.example.test/token", Scopes = ["patient/Patient.read"]
        }, request =>
        {
            requests++;
            Assert.Equal("client_credentials", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Split('&')[0].Split('=')[1]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}") };
        });

        Assert.Equal("synthetic-token", await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("synthetic-token", await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SmartBackendServicesBuildsSignedClientAssertion()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.Combine(Path.GetTempPath(), $"fhiril-test-key-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());
        try
        {
            string? assertion = null;
            var provider = Provider(new OAuthOptions
            {
                Mode = "SmartBackendServices", ClientId = "synthetic-backend", TokenEndpoint = "https://auth.example.test/token",
                SigningKeyPath = keyPath, SigningKeyId = "test-key", Scopes = ["system/Patient.read"]
            }, request =>
            {
                var form = Uri.UnescapeDataString(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Contains("client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form);
                assertion = form.Split('&').Single(item => item.StartsWith("client_assertion=", StringComparison.Ordinal))["client_assertion=".Length..];
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"synthetic-token\",\"expires_in\":300,\"token_type\":\"Bearer\"}") };
            });

            Assert.Equal("synthetic-token", await provider.GetAccessTokenAsync(CancellationToken.None));
            var jwt = new JsonWebTokenHandler().ReadJsonWebToken(assertion);
            Assert.Equal("synthetic-backend", jwt.Issuer);
            Assert.Equal("synthetic-backend", jwt.Subject);
            Assert.Contains("https://auth.example.test/token", jwt.Audiences);
            Assert.Equal("RS256", jwt.Alg);
            Assert.NotEmpty(jwt.GetClaim("jti").Value);
            using var publicKey = RSA.Create();
            publicKey.ImportParameters(rsa.ExportParameters(false));
            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(assertion!,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "synthetic-backend",
                    ValidateAudience = true,
                    ValidAudience = "https://auth.example.test/token",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(publicKey),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(5)
                });
            Assert.True(validated.IsValid);
            Assert.Equal("synthetic-backend", validated.ClaimsIdentity?.FindFirst("sub")?.Value);
        }
        finally { File.Delete(keyPath); }
    }

    [Fact]
    public async Task ScopePolicyRejectsMissingAndAcceptsRequiredScope()
    {
        var handler = new ScopeHandler(Options.Create(new OAuthOptions { Mode = "InteractiveOidc" }));
        var requirement = new ScopeRequirement("patient/Patient.read");
        var noScope = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity("test")), null);
        await handler.HandleAsync(noScope);
        Assert.False(noScope.HasSucceeded);

        var withScope = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim("scope", "patient/Patient.read")], "test")), null);
        await handler.HandleAsync(withScope);
        Assert.True(withScope.HasSucceeded);
    }

    private static OAuthTokenProvider Provider(OAuthOptions options, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubFactory(handler), Options.Create(options), new HttpContextAccessor(), NullLogger<OAuthTokenProvider>.Instance);

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> callback) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(callback));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }

    [Fact]
    public async Task BearerHandlerAddsTokenOnlyWhenProviderReturnsOne()
    {
        string? authorization = null;
        using var anonymous = new BearerTokenHandler(new StaticTokenProvider(null), new HttpContextAccessor())
        {
            InnerHandler = new CaptureHandler(request => { authorization = request.Headers.Authorization?.ToString(); return new HttpResponseMessage(HttpStatusCode.OK); })
        };
        using var anonClient = new HttpClient(anonymous);
        using var anonResponse = await anonClient.GetAsync("https://fhir.example.test/fhir/metadata");
        Assert.Null(authorization);

        using var authenticated = new BearerTokenHandler(new StaticTokenProvider("token-for-test"), new HttpContextAccessor())
        {
            InnerHandler = new CaptureHandler(request => { authorization = request.Headers.Authorization?.ToString(); return new HttpResponseMessage(HttpStatusCode.OK); })
        };
        using var authClient = new HttpClient(authenticated);
        using var authResponse = await authClient.GetAsync("https://fhir.example.test/fhir/metadata");
        Assert.Equal("Bearer token-for-test", authorization);
    }

    [Fact]
    public async Task PatientWriteIsBlockedByDefaultBeforeGatewayIsCalled()
    {
        var gateway = new NeverWriteService();
        var controller = new FhirController(gateway,
            Options.Create(new FhirOptions { AllowWrites = false }),
            Options.Create(new OAuthOptions { Mode = "Anonymous" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreatePatient(
            new CreatePatientRequest { FamilyName = "Synthetic", GivenName = "Test" },
            "I understand this modifies HAPI data", CancellationToken.None));
        Assert.Equal(0, gateway.WriteCalls);
    }

    private sealed class StaticTokenProvider(string? token) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }

    private sealed class NeverWriteService : IFhirResourceService
    {
        public int WriteCalls { get; private set; }
        public Task<Bundle> SearchPatientsAsync(string? identifier, string? name, int count, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Bundle> SearchEncountersAsync(string? patient, int count, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Bundle> SearchObservationsAsync(string? patient, int count, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<T?> ReadAsync<T>(string resourceType, string id, CancellationToken cancellationToken) where T : Resource => throw new NotImplementedException();
        public Task<T> CreateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource { WriteCalls++; throw new NotImplementedException(); }
        public Task<T> UpdateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource { WriteCalls++; throw new NotImplementedException(); }
        public Task DeleteAsync(string resourceType, string id, string versionId, CancellationToken cancellationToken) { WriteCalls++; throw new NotImplementedException(); }
    }
}
