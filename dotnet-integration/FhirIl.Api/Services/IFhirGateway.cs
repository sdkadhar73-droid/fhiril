using Hl7.Fhir.Model;

namespace FhirIl.Api.Services;

public interface IFhirGateway
{
    Task<Bundle> SearchPatientsAsync(string? identifier, string? name, int count, CancellationToken cancellationToken);
    Task<Bundle> SearchEncountersAsync(string? patient, int count, CancellationToken cancellationToken);
    Task<Bundle> SearchObservationsAsync(string? patient, int count, CancellationToken cancellationToken);
    Task<T?> ReadAsync<T>(string resourceType, string id, CancellationToken cancellationToken) where T : Resource;
    Task<T> CreateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    Task<T> UpdateAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource;
    System.Threading.Tasks.Task DeleteAsync(string resourceType, string id, string versionId, CancellationToken cancellationToken);
}
