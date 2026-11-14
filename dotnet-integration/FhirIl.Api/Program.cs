using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using FhirIl.Api.Infrastructure;
using FhirIl.Api.Services;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<FhirOptions>(builder.Configuration.GetSection(FhirOptions.SectionName));
builder.Services.Configure<OAuthOptions>(builder.Configuration.GetSection(OAuthOptions.SectionName));
builder.Services.Configure<SmartSandboxOptions>(builder.Configuration.GetSection(SmartSandboxOptions.SectionName));
builder.Services.Configure<LocalOAuthOptions>(builder.Configuration.GetSection(LocalOAuthOptions.SectionName));
builder.Services.AddHttpClient("oauth", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("smart-sandbox", client =>
{
    var timeout = Math.Clamp(builder.Configuration.GetValue("SmartSandbox:TimeoutSeconds", 15), 1, 120);
    client.Timeout = TimeSpan.FromSeconds(timeout);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/fhir+json");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("fhir-discovery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Fhir:TimeoutSeconds", 15), 1, 120));
    client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+json, application/json");
});
builder.Services.AddSingleton<IAccessTokenProvider, OAuthTokenProvider>();
builder.Services.AddSingleton<ISmartSandboxSessionStore, SmartSandboxSessionStore>();
builder.Services.AddScoped<IFhirGateway, FirelyFhirGateway>();
builder.Services.AddScoped<IFhirResourceService, FhirResourceService>();
builder.Services.AddScoped<FhirServerDiscovery>();
builder.Services.AddScoped<SmartSandboxDiscovery>();
builder.Services.AddScoped<SmartSandboxFhirClient>();
builder.Services.AddSingleton<IAuthorizationHandler, ScopeHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

var oauth = builder.Configuration.GetSection(OAuthOptions.SectionName).Get<OAuthOptions>() ?? new OAuthOptions();
var localOAuth = builder.Configuration.GetSection(LocalOAuthOptions.SectionName).Get<LocalOAuthOptions>() ?? new LocalOAuthOptions();

if (localOAuth.Enabled)
{
    builder.Services.AddOpenIddict()
        .AddServer(options =>
        {
            options.EnableDegradedMode();
            options.SetIssuer(new Uri(localOAuth.Issuer, UriKind.Absolute));
            options.SetAuthorizationEndpointUris("/connect/authorize");
            options.SetTokenEndpointUris("/connect/token");
            options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
            options.AllowClientCredentialsFlow();
            options.RegisterScopes(localOAuth.Scopes);
            options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler => handler.UseInlineHandler(context =>
            {
                if (string.Equals(context.ClientId, localOAuth.ClientId, StringComparison.Ordinal)) context.SkipRequest();
                return default;
            }));
            options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler => handler.UseInlineHandler(context =>
            {
                if (string.Equals(context.ClientId, localOAuth.ClientId, StringComparison.Ordinal) ||
                    string.Equals(context.ClientId, localOAuth.MachineClientId, StringComparison.Ordinal)) context.SkipRequest();
                return default;
            }));
            options.AddDevelopmentEncryptionCertificate();
            options.AddDevelopmentSigningCertificate();
            options.UseAspNetCore()
                .EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough()
                .DisableTransportSecurityRequirement();
        })
        .AddValidation(options =>
        {
            options.SetIssuer(new Uri(localOAuth.Issuer, UriKind.Absolute));
            options.AddAudiences(localOAuth.Audience);
            options.UseLocalServer();
            options.UseAspNetCore();
        });
}

switch (oauth.Mode)
{
    case "Anonymous":
        builder.Services.AddAuthentication("DevelopmentAnonymous")
            .AddScheme<AuthenticationSchemeOptions, DevelopmentAnonymousHandler>("DevelopmentAnonymous", _ => { });
        break;
    case "InteractiveOidc":
        if (string.IsNullOrWhiteSpace(oauth.Authority) || string.IsNullOrWhiteSpace(oauth.ClientId))
            throw new InvalidOperationException("InteractiveOidc requires OAuth:Authority and OAuth:ClientId.");
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = oauth.Authority;
            options.ClientId = oauth.ClientId;
            options.ClientSecret = oauth.ClientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear();
            foreach (var scope in oauth.Scopes) options.Scope.Add(scope);
        });
        break;
    case "ClientCredentials":
    case "SmartBackendServices":
        if (string.IsNullOrWhiteSpace(oauth.Authority) || string.IsNullOrWhiteSpace(oauth.ApiAudience))
            throw new InvalidOperationException("Authenticated API modes require OAuth:Authority and OAuth:ApiAudience to validate incoming API tokens.");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = oauth.Authority;
                options.Audience = oauth.ApiAudience;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true };
            });
        break;
    default:
        throw new InvalidOperationException("OAuth:Mode must be Anonymous, InteractiveOidc, ClientCredentials or SmartBackendServices.");
}

if (localOAuth.Enabled)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "LocalApi";
        options.DefaultChallengeScheme = "LocalApi";
    })
    .AddPolicyScheme("LocalApi", "Local cookie or bearer authentication", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.Any(value => value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                : "LocalCookie";
    })
    .AddCookie("LocalCookie");
}

var smartSandbox = builder.Configuration.GetSection(SmartSandboxOptions.SectionName).Get<SmartSandboxOptions>() ?? new SmartSandboxOptions();
builder.Services.AddAuthentication()
    .AddCookie("SmartSandboxCookie", options =>
    {
        options.Cookie.Name = ".FhirIL.SmartSandbox";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = false;
    })
    .AddOAuth<SmartPublicOAuthOptions, SmartPublicOAuthHandler>("SmartSandbox", options =>
    {
        options.SignInScheme = "SmartSandboxCookie";
        options.ClientId = smartSandbox.ClientId;
        options.AuthorizationEndpoint = smartSandbox.AuthorizationEndpoint;
        options.TokenEndpoint = smartSandbox.TokenEndpoint;
        options.CallbackPath = smartSandbox.CallbackPath;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Scope.Clear();
        foreach (var scope in smartSandbox.Scopes) options.Scope.Add(scope);
        options.AdditionalAuthorizationParameters["aud"] = smartSandbox.Issuer;
        options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
        {
            OnCreatingTicket = context =>
            {
                var sessionId = context.Properties.Items.TryGetValue("smart_session_id", out var value) ? value : null;
                var tokenJson = context.TokenResponse.Response?.RootElement ?? throw new InvalidOperationException("The SMART Sandbox token response was not valid JSON.");
                var patientId = tokenJson.TryGetProperty("patient", out var patientValue) && patientValue.ValueKind == System.Text.Json.JsonValueKind.String
                    ? patientValue.GetString()
                    : null;
                var scope = tokenJson.TryGetProperty("scope", out var scopeValue) && scopeValue.ValueKind == System.Text.Json.JsonValueKind.String
                    ? scopeValue.GetString()
                    : string.Join(' ', smartSandbox.Scopes);
                var validExpiry = int.TryParse(context.TokenResponse.ExpiresIn, out var expiresInSeconds) && expiresInSeconds > 0;
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(context.TokenResponse.AccessToken) ||
                    !Regex.IsMatch(patientId ?? "", "^[A-Za-z0-9.-]{1,64}$", RegexOptions.CultureInvariant) ||
                    !scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("patient/Patient.r", StringComparer.Ordinal) ||
                    !validExpiry)
                    throw new InvalidOperationException("The SMART Sandbox token response is missing a valid Patient context, read scope or expiry.");

                var session = new SmartSandboxSession(context.TokenResponse.AccessToken, patientId!, smartSandbox.Issuer, scope!, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds));
                var store = context.HttpContext.RequestServices.GetRequiredService<ISmartSandboxSessionStore>();
                store.Store(sessionId!, session);
                context.Properties.ExpiresUtc = session.ExpiresAt;
                var identity = (ClaimsIdentity)context.Principal!.Identity!;
                identity.AddClaim(new Claim("smart_session_id", sessionId!));
                identity.AddClaim(new Claim("smart_patient_id", patientId!));
                identity.AddClaim(new Claim("smart_scope", scope!));
                identity.AddClaim(new Claim("smart_issuer", smartSandbox.Issuer));
                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("SmartSandboxOAuth").LogWarning("SMART Sandbox authorization failed. CorrelationId={CorrelationId} ExceptionType={ExceptionType}",
                        context.HttpContext.TraceIdentifier, context.Failure?.GetType().Name ?? "OAuthFailure");
                context.HandleResponse();
                context.Response.Redirect("/smart?error=authorization_failed");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PatientRead", policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("patient/Patient.read")));
    options.AddPolicy("EncounterRead", policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("patient/Encounter.read")));
    options.AddPolicy("ObservationRead", policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("patient/Observation.read")));
    options.AddPolicy("PatientWrite", policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement("patient/Patient.write")));
});
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier; return Task.CompletedTask; });
    await next();
});
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); app.MapOpenApi(); }
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program;
