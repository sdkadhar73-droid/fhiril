using Hl7.Fhir.Model;

namespace FhirIl.Api.Services;

public sealed class FhirResourceService(IFhirGateway gateway) : IFhirResourceService
{
    public Task<Bundle> SearchPatientsAsync(string? identifier, string? name, int count, CancellationToken cancellationToken) =>
        gateway.SearchPatientsAsync(identifier, name, count, cancellationToken);
    public Task<Bundle> SearchEncountersAsync(string? patient, int count, CancellationToken cancellationToken) =>
        gateway.SearchEncountersAsync(patient, count, cancellationToken);
    public Task<Bundle> SearchObservationsAsync(string? patient, int count, CancellationToken cancellationToken) =>
        gateway.SearchObservationsAsync(patient, count, cancellationToken);
    public Task<T?> ReadAsync<T>(string resourceType, string id, CancellationToken cancellationToken) where T : Resource =>
        gateway.ReadAsync<T>(resourceType, id, cancellationToken);
    public Task<T> CreateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
        gateway.CreateAsync(resource, cancellationToken);
    public Task<T> UpdateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource =>
        gateway.UpdateAsync(resource, cancellationToken);
    public System.Threading.Tasks.Task DeleteAsync(string resourceType, string id, string versionId, CancellationToken cancellationToken) =>
        gateway.DeleteAsync(resourceType, id, versionId, cancellationToken);
}
