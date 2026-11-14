using System.Diagnostics;
using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using FhirIl.Api.Infrastructure;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Services;

public sealed class FirelyFhirGateway(
    IAccessTokenProvider tokenProvider,
    IHttpContextAccessor httpContextAccessor,
    IOptions<FhirOptions> options,
    ILogger<FirelyFhirGateway> logger) : IFhirGateway
{
    private readonly Uri _baseUri = ValidateBaseUrl(options.Value.BaseUrl);

    public Task<Bundle> SearchPatientsAsync(string? identifier, string? name, int count, CancellationToken cancellationToken)
    {
        var criteria = new List<string> { $"_count={Math.Clamp(count, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(identifier)) criteria.Add($"identifier={Uri.EscapeDataString(identifier)}");
        if (!string.IsNullOrWhiteSpace(name)) criteria.Add($"name={Uri.EscapeDataString(name)}");
        return ExecuteAsync("Patient.search", async ct => await Client("Patient.search").SearchAsync<Patient>(criteria.ToArray(), ct: ct) ?? new Bundle { Type = Bundle.BundleType.Searchset }, cancellationToken);
    }

    public Task<Bundle> SearchEncountersAsync(string? patient, int count, CancellationToken cancellationToken) =>
        SearchAsync<Encounter>("Encounter", patient, count, cancellationToken);

    public Task<Bundle> SearchObservationsAsync(string? patient, int count, CancellationToken cancellationToken) =>
        SearchAsync<Observation>("Observation", patient, count, cancellationToken);

    private Task<Bundle> SearchAsync<T>(string operation, string? patient, int count, CancellationToken cancellationToken) where T : Resource, new()
    {
        var criteria = new List<string> { $"_count={Math.Clamp(count, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(patient)) criteria.Add($"patient={Uri.EscapeDataString(patient)}");
        return ExecuteAsync(operation + ".search", async ct => await Client(operation + ".search").SearchAsync<T>(criteria.ToArray(), ct: ct) ?? new Bundle { Type = Bundle.BundleType.Searchset }, cancellationToken);
    }

    public Task<T?> ReadAsync<T>(string resourceType, string id, CancellationToken cancellationToken) where T : Resource =>
        ExecuteAsync(resourceType + ".read", ct => Client(resourceType + ".read").ReadAsync<T>(new Uri(_baseUri, $"{resourceType}/{Uri.EscapeDataString(id)}"), ct: ct), cancellationToken);

    public Task<T> CreateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
        ExecuteAsync(resource.TypeName + ".create", async ct => await Client(resource.TypeName + ".create").CreateAsync(resource, ct) ?? throw new InvalidOperationException("FHIR create returned no resource."), cancellationToken);

    public Task<T> UpdateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
        ExecuteAsync(resource.TypeName + ".update", async ct => await Client(resource.TypeName + ".update").UpdateAsync(resource, versionAware: true, ct: ct) ?? throw new InvalidOperationException("FHIR update returned no resource."), cancellationToken);

    public System.Threading.Tasks.Task DeleteAsync(string resourceType, string id, string versionId, CancellationToken cancellationToken) =>
        ExecuteAsync(resourceType + ".delete", async ct => { await Client(resourceType + ".delete", versionId).DeleteAsync($"{resourceType}/{Uri.EscapeDataString(id)}", ct); return true; }, cancellationToken);

    private FhirClient Client(string operation, string? expectedVersionId = null)
    {
        HttpMessageHandler inner = new ReadRetryHandler { InnerHandler = new HttpClientHandler() };
        if (!string.IsNullOrWhiteSpace(expectedVersionId)) inner = new IfMatchHandler(expectedVersionId) { InnerHandler = inner };
        inner = new FhirHttpLoggingHandler(operation, logger, httpContextAccessor) { InnerHandler = inner };
        var handler = new BearerTokenHandler(tokenProvider, httpContextAccessor) { InnerHandler = inner };
        return new FhirClient(_baseUri, new FhirClientSettings
        {
            PreferredFormat = ResourceFormat.Json,
            Timeout = Math.Clamp(options.Value.TimeoutSeconds, 1, 120) * 1000,
            VerifyFhirVersion = true
        }, handler);
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("FHIR operation failed. CorrelationId={CorrelationId} Operation={Operation} ExceptionType={ExceptionType}", httpContextAccessor.HttpContext?.TraceIdentifier, operation, exception.GetType().Name);
            throw;
        }
    }

    private static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("LocalHapi:BaseUrl must be an absolute HTTP or HTTPS URL.");
        return new Uri(uri.ToString().TrimEnd('/') + "/");
    }
}

internal sealed class IfMatchHandler(string versionId) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"W/\"{versionId}\"");
        return base.SendAsync(request, cancellationToken);
    }
}
