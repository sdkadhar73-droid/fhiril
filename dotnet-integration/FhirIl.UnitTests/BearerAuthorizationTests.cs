using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FhirIl.UnitTests;

public sealed class BearerAuthorizationTests
{
    private const string Issuer = "https://auth.test.example";
    private const string Audience = "fhiril-api";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("test-only-signing-key-material-32bytes!"));

    [Fact]
    public async Task ValidBearerTokenWithRequiredScopeIsAccepted()
    {
        await using var app = await CreateServerAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateToken("patient/Patient.read", DateTime.UtcNow.AddMinutes(5)));
        var response = await client.GetAsync("/secure");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingInvalidAndExpiredTokensAreRejected()
    {
        await using var app = await CreateServerAsync();
        using var client = app.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/secure")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/secure")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateToken("patient/Patient.read", DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/secure")).StatusCode);
    }

    [Fact]
    public async Task TokenWithoutRequiredScopeIsForbidden()
    {
        await using var app = await CreateServerAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateToken("patient/Observation.read", DateTime.UtcNow.AddMinutes(5)));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/secure")).StatusCode);
    }

    private static string CreateToken(string scope, DateTime expires)
    {
        var notBefore = expires <= DateTime.UtcNow ? expires.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(-1);
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim("scope", scope)], notBefore, expires,
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<WebApplication> CreateServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthorizationHandler, ScopeHandler>();
        builder.Services.Configure<OAuthOptions>(options => options.Mode = "ClientCredentials");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("PatientRead", policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("patient/Patient.read"))));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/secure", () => Results.Ok()).RequireAuthorization("PatientRead");
        await app.StartAsync();
        return app;
    }
}
