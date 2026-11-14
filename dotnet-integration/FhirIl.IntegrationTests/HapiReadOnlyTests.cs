using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using FhirIl.Api.Services;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Task = System.Threading.Tasks.Task;

namespace FhirIl.IntegrationTests;

public sealed class HapiReadOnlyTests
{
    [Fact]
    public async Task HapiR4MetadataResourceReadsAndSearches()
    {
        var baseUrl = Environment.GetEnvironmentVariable("FHIRIL_FHIR_BASE_URL") ?? "http://localhost:8080/fhir";
        var options = Options.Create(new FhirOptions { BaseUrl = baseUrl, TimeoutSeconds = 15 });
        var gateway = new FirelyFhirGateway(new AnonymousTokenProvider(), new HttpContextAccessor(), options, NullLogger<FirelyFhirGateway>.Instance);
        var discovery = new FhirServerDiscovery(new DiscoveryHttpClientFactory(), options);

        var server = await discovery.GetAsync(CancellationToken.None);
        Assert.Equal("4.0.1", server.FhirVersion);
        Assert.Contains(server.ResourceCapabilities, item => item.ResourceType == "Patient");
        Assert.Contains(server.ResourceCapabilities, item => item.ResourceType == "Encounter");
        Assert.Contains(server.ResourceCapabilities, item => item.ResourceType == "Observation");
        Assert.Contains(server.ResourceCapabilities.Single(item => item.ResourceType == "Patient").Interactions,
            interaction => interaction.Equals("Read", StringComparison.OrdinalIgnoreCase));

        var patient = await gateway.ReadAsync<Patient>("Patient", "1", CancellationToken.None);
        Assert.NotNull(patient);
        Assert.Equal("1", patient.Id);
        Assert.Contains(patient.Identifier, item => item.System == "https://fhiril.example.org/identifiers/ecrnow-readback" && item.Value == "FHIRIL-ECRNOW-READBACK-3a0890947002d14ea12193747a2454ea");

        var patients = await gateway.SearchPatientsAsync(null, null, 20, CancellationToken.None);
        var encounters = await gateway.SearchEncountersAsync(null, 20, CancellationToken.None);
        var observations = await gateway.SearchObservationsAsync(null, 20, CancellationToken.None);
        Assert.NotNull(patients);
        Assert.NotNull(encounters);
        Assert.NotNull(observations);
        Assert.Equal("not_advertised", server.SmartStatus);
    }

    private sealed class AnonymousTokenProvider : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class DiscoveryHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(15) };
    }
}
