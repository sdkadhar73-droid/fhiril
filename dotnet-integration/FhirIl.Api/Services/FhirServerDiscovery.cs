using System.Net;
using System.Text.Json;
using FhirIl.Api.Configuration;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Services;

public sealed class FhirServerDiscovery(IHttpClientFactory clients, IOptions<FhirOptions> options)
{
    private readonly Uri _baseUri = new(options.Value.BaseUrl.TrimEnd('/') + "/");

    public async Task<ServerDiscoveryResult> GetAsync(CancellationToken ct)
    {
        var client = clients.CreateClient("fhir-discovery");
        var metadataResponse = await client.GetAsync(new Uri(_baseUri, "metadata"), ct);
        var metadataBody = await metadataResponse.Content.ReadAsStringAsync(ct);
        metadataResponse.EnsureSuccessStatusCode();
        var capability = new FhirJsonDeserializer().Deserialize<CapabilityStatement>(metadataBody);

        var smart = await ReadSmartAsync(client, new Uri(_baseUri, ".well-known/smart-configuration"), ct);
        var extensions = capability.Rest?.SelectMany(r => r.Security?.Extension ?? []).
            Where(e => !string.IsNullOrEmpty(e.Url) && (e.Url.Contains("oauth", StringComparison.OrdinalIgnoreCase) || e.Url.Contains("smart", StringComparison.OrdinalIgnoreCase)))
            .Select(e => new { e.Url, Value = e.Value?.ToString() }).ToArray() ?? [];
        var resources = capability.Rest?.SelectMany(rest => rest.Resource ?? [])
            .Select(resource => new ResourceCapability(
                resource.Type?.ToString() ?? "unknown",
                resource.Interaction?.Select(interaction => interaction.Code?.ToString() ?? "unknown").ToArray() ?? [],
                resource.Operation?.Select(operation => operation.Name).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? []))
            .ToArray() ?? [];
        var systemInteractions = capability.Rest?.SelectMany(rest => rest.Interaction ?? [])
            .Select(interaction => interaction.Code?.ToString() ?? "unknown").Distinct().ToArray() ?? [];
        var systemOperations = capability.Rest?.SelectMany(rest => rest.Operation ?? [])
            .Select(operation => operation.Name).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray() ?? [];
        return new ServerDiscoveryResult(
            "available", metadataResponse.StatusCode, NormalizeFhirVersion(capability.FhirVersion?.ToString()),
            capability.Implementation?.Description,
            smart.Status, smart.AuthorizationEndpoint, smart.TokenEndpoint, smart.RegistrationEndpoint,
            smart.GrantTypes, smart.Scopes, smart.Capabilities, smart.CodeChallengeMethods,
            smart.TokenEndpointAuthMethods, extensions, resources, systemInteractions, systemOperations);
    }

    private static string? NormalizeFhirVersion(string? value) => value switch
    {
        "N4_0_1" => "4.0.1",
        "N4_3_0" => "4.3.0",
        "N5_0_0" => "5.0.0",
        _ => value
    };

    private static async Task<SmartDiscovery> ReadSmartAsync(HttpClient client, Uri smartUri, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(smartUri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return SmartDiscovery.Unavailable("not_advertised");
            if (!response.IsSuccessStatusCode) return SmartDiscovery.Unavailable("endpoint_unavailable");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? Str(string key) => root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
            string[] Arr(string key) => root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array
                ? prop.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
            return new SmartDiscovery("advertised", Str("authorization_endpoint"), Str("token_endpoint"), Str("registration_endpoint"),
                Arr("grant_types_supported"), Arr("scopes_supported"), Arr("capabilities"), Arr("code_challenge_methods_supported"), Arr("token_endpoint_auth_methods_supported"));
        }
        catch (JsonException) { return SmartDiscovery.Unavailable("invalid_response"); }
        catch (HttpRequestException) { return SmartDiscovery.Unavailable("endpoint_unavailable"); }
    }

    private sealed record SmartDiscovery(string Status, string? AuthorizationEndpoint, string? TokenEndpoint, string? RegistrationEndpoint,
        string[] GrantTypes, string[] Scopes, string[] Capabilities, string[] CodeChallengeMethods, string[] TokenEndpointAuthMethods)
    {
        public static SmartDiscovery Unavailable(string status) => new(status, null, null, null, [], [], [], [], []);
    }
}

public sealed record ServerDiscoveryResult(string Connectivity, HttpStatusCode MetadataStatus, string? FhirVersion, string? ImplementationDescription,
    string SmartStatus, string? AuthorizationEndpoint, string? TokenEndpoint, string? RegistrationEndpoint, string[] GrantTypes,
    string[] Scopes, string[] Capabilities, string[] PkceMethods, string[] TokenEndpointAuthMethods, object[] CapabilityExtensions,
    ResourceCapability[] ResourceCapabilities, string[] SystemInteractions, string[] SystemOperations);

public sealed record ResourceCapability(string ResourceType, string[] Interactions, string[] Operations);
