using System.Net;
using System.Text.Json;
using FhirIl.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Services;

public sealed class SmartSandboxDiscovery(IHttpClientFactory clients, IOptions<SmartSandboxOptions> options)
{
    private readonly SmartSandboxOptions _options = options.Value;

    public async Task<SmartSandboxDiscoveryResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new InvalidOperationException("SMART Sandbox integration is disabled.");
        var issuer = SmartSandboxIssuer.Validate(_options.Issuer);
        var configuredAuthorization = SmartSandboxIssuer.ValidateEndpoint(_options.AuthorizationEndpoint, issuer, "/auth/authorize");
        var configuredToken = SmartSandboxIssuer.ValidateEndpoint(_options.TokenEndpoint, issuer, "/auth/token");
        var client = clients.CreateClient("smart-sandbox");

        using var smartResponse = await client.GetAsync(new Uri(issuer, ".well-known/smart-configuration"), cancellationToken);
        if (!smartResponse.IsSuccessStatusCode)
            throw new HttpRequestException("SMART discovery endpoint did not return a successful response.", null, smartResponse.StatusCode);
        using var document = JsonDocument.Parse(await smartResponse.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var metadataIssuer = ReadString(root, "issuer");
        var authorization = ReadString(root, "authorization_endpoint");
        var token = ReadString(root, "token_endpoint");
        if (!string.Equals(metadataIssuer?.TrimEnd('/'), issuer.ToString().TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException("SMART discovery issuer did not match the configured FHIR issuer.");
        SmartSandboxIssuer.RequireExactEndpoint(authorization, configuredAuthorization);
        SmartSandboxIssuer.RequireExactEndpoint(token, configuredToken);

        var grants = ReadArray(root, "grant_types_supported");
        var pkce = ReadArray(root, "code_challenge_methods_supported");
        var scopes = ReadArray(root, "scopes_supported");
        var capabilities = ReadArray(root, "capabilities");
        if (!grants.Contains("authorization_code", StringComparer.Ordinal))
            throw new InvalidOperationException("The SMART Sandbox does not advertise authorization_code.");
        if (!pkce.Contains("S256", StringComparer.Ordinal))
            throw new InvalidOperationException("The SMART Sandbox does not advertise PKCE S256.");
        if (!scopes.Contains("launch/patient", StringComparer.Ordinal))
            throw new InvalidOperationException("The SMART Sandbox does not advertise standalone patient context.");
        var requiredCapabilities = new[] { "launch-standalone", "client-public", "context-standalone-patient", "permission-patient" };
        if (requiredCapabilities.Any(capability => !capabilities.Contains(capability, StringComparer.Ordinal)))
            throw new InvalidOperationException("The SMART Sandbox does not advertise all required standalone patient-launch capabilities.");
        if (!scopes.Contains("patient/Patient.r", StringComparer.Ordinal) &&
            !(scopes.Contains("patient/*.*", StringComparer.Ordinal) && capabilities.Contains("permission-v2", StringComparer.Ordinal)))
            throw new InvalidOperationException("The SMART Sandbox does not advertise the Patient resource permission model required for a least-privilege R4 read.");

        using var metadataResponse = await client.GetAsync(new Uri(issuer, "metadata"), cancellationToken);
        if (!metadataResponse.IsSuccessStatusCode)
            throw new HttpRequestException("FHIR CapabilityStatement endpoint did not return a successful response.", null, metadataResponse.StatusCode);
        using var capabilityStatement = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
        var fhirVersion = ReadString(capabilityStatement.RootElement, "fhirVersion");
        if (fhirVersion is not ("4.0.0" or "4.0.1")) throw new InvalidOperationException("The SMART Sandbox CapabilityStatement did not identify a FHIR R4 version.");

        return new SmartSandboxDiscoveryResult(issuer.ToString().TrimEnd('/'), configuredAuthorization.ToString(), configuredToken.ToString(), fhirVersion, grants, pkce, scopes, capabilities);
    }

    private static string? ReadString(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] ReadArray(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
        : [];
}

public sealed record SmartSandboxDiscoveryResult(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string FhirVersion,
    string[] GrantTypes, string[] PkceMethods, string[] Scopes, string[] Capabilities);

public static class SmartSandboxIssuer
{
    private const string ExpectedIssuer = "https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/fhir";

    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(issuer.Host, "launch.smarthealthit.org", StringComparison.OrdinalIgnoreCase) ||
            !issuer.IsDefaultPort || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment) ||
            !string.Equals(issuer.AbsoluteUri.TrimEnd('/'), ExpectedIssuer, StringComparison.Ordinal))
            throw new ArgumentException("The SMART Sandbox issuer is not the approved FHIR R4 issuer.", nameof(value));
        return new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/");
    }

    public static Uri ValidateEndpoint(string value, Uri issuer, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(endpoint.Host, issuer.Host, StringComparison.OrdinalIgnoreCase) || !endpoint.IsDefaultPort ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !endpoint.AbsolutePath.StartsWith(issuer.AbsolutePath[..issuer.AbsolutePath.LastIndexOf("/fhir", StringComparison.Ordinal)] + suffix, StringComparison.Ordinal))
            throw new ArgumentException("A SMART OAuth endpoint is outside the approved sandbox issuer path.", nameof(value));
        return endpoint;
    }

    public static void RequireExactEndpoint(string? discovered, Uri configured)
    {
        if (!Uri.TryCreate(discovered, UriKind.Absolute, out var endpoint) || endpoint != configured)
            throw new InvalidOperationException("A discovered SMART OAuth endpoint did not match the configured sandbox endpoint.");
    }
}
