using FhirIl.Api.Configuration;
using FhirIl.Api.Models;
using FhirIl.Api.Services;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Controllers;

[ApiController]
[Route("api/fhir")]
public sealed class FhirController(IFhirResourceService fhir, IOptions<FhirOptions> fhirOptions, IOptions<FhirIl.Api.Configuration.OAuthOptions> oauthOptions) : ControllerBase
{
    private readonly FhirOptions _fhirOptions = fhirOptions.Value;
    private readonly FhirIl.Api.Configuration.OAuthOptions _oauthOptions = oauthOptions.Value;

    [HttpGet("patients")]
    [Authorize(Policy = "PatientRead")]
    public async Task<IActionResult> SearchPatients([FromQuery] string? identifier, [FromQuery] string? name, [FromQuery] int count = 20, CancellationToken ct = default) =>
        Fhir(await fhir.SearchPatientsAsync(identifier, name, count, ct));

    [HttpGet("patients/{id}")]
    [Authorize(Policy = "PatientRead")]
    public async Task<IActionResult> ReadPatient(string id, CancellationToken ct)
    {
        var resource = await fhir.ReadAsync<Patient>("Patient", id, ct);
        return resource is null ? NotFound() : Fhir(resource);
    }

    [HttpGet("encounters")]
    [Authorize(Policy = "EncounterRead")]
    public async Task<IActionResult> SearchEncounters([FromQuery] string? patient, [FromQuery] int count = 20, CancellationToken ct = default) =>
        Fhir(await fhir.SearchEncountersAsync(patient, count, ct));

    [HttpGet("encounters/{id}")]
    [Authorize(Policy = "EncounterRead")]
    public async Task<IActionResult> ReadEncounter(string id, CancellationToken ct)
    {
        var resource = await fhir.ReadAsync<Encounter>("Encounter", id, ct);
        return resource is null ? NotFound() : Fhir(resource);
    }

    [HttpGet("observations")]
    [Authorize(Policy = "ObservationRead")]
    public async Task<IActionResult> SearchObservations([FromQuery] string? patient, [FromQuery] int count = 20, CancellationToken ct = default) =>
        Fhir(await fhir.SearchObservationsAsync(patient, count, ct));

    [HttpGet("observations/{id}")]
    [Authorize(Policy = "ObservationRead")]
    public async Task<IActionResult> ReadObservation(string id, CancellationToken ct)
    {
        var resource = await fhir.ReadAsync<Observation>("Observation", id, ct);
        return resource is null ? NotFound() : Fhir(resource);
    }

    [HttpPost("patients")]
    [Authorize(Policy = "PatientWrite")]
    public async Task<IActionResult> CreatePatient([FromBody] CreatePatientRequest request, [FromHeader(Name = "X-FHIRIL-Confirm-Write")] string? confirmation, CancellationToken ct = default)
    {
        EnsureWriteAllowed(confirmation);
        if ((request.IdentifierSystem is null) != (request.IdentifierValue is null))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Identifier system and value must be provided together." });
        var patient = new Patient { Name = [new HumanName { Family = request.FamilyName, Given = [request.GivenName] }] };
        if (request.IdentifierSystem is not null)
            patient.Identifier = [new Identifier(request.IdentifierSystem, request.IdentifierValue)];
        var created = await fhir.CreateAsync(patient, ct);
        Response.Headers.Location = $"/api/fhir/patients/{created.Id}";
        var result = Content(FhirContent(created), "application/fhir+json");
        result.StatusCode = StatusCodes.Status201Created;
        return result;
    }

    [HttpPut("patients/{id}")]
    [Authorize(Policy = "PatientWrite")]
    public async Task<IActionResult> UpdatePatient(string id, [FromBody] CreatePatientRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "X-FHIRIL-Confirm-Write")] string? confirmation, CancellationToken ct)
    {
        EnsureWriteAllowed(confirmation);
        var existing = await fhir.ReadAsync<Patient>("Patient", id, ct);
        if (existing is null) return NotFound();
        if (!VersionMatches(ifMatch, existing.Meta?.VersionId))
            return StatusCode(StatusCodes.Status412PreconditionFailed, new ProblemDetails { Status = 412, Title = "If-Match must contain the current FHIR version." });
        existing.Name = [new HumanName { Family = request.FamilyName, Given = [request.GivenName] }];
        var updated = await fhir.UpdateAsync(existing, ct);
        return Fhir(updated);
    }

    [HttpDelete("patients/{id}")]
    [Authorize(Policy = "PatientWrite")]
    public async Task<IActionResult> DeletePatient(string id, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "X-FHIRIL-Confirm-Write")] string? confirmation, CancellationToken ct)
    {
        EnsureWriteAllowed(confirmation);
        var existing = await fhir.ReadAsync<Patient>("Patient", id, ct);
        if (existing is null) return NotFound();
        if (!VersionMatches(ifMatch, existing.Meta?.VersionId))
            return StatusCode(StatusCodes.Status412PreconditionFailed, new ProblemDetails { Status = 412, Title = "If-Match must contain the current FHIR version." });
        await fhir.DeleteAsync("Patient", id, existing.Meta?.VersionId ?? "", ct);
        return NoContent();
    }

    private void EnsureWriteAllowed(string? confirmation)
    {
        if (!_fhirOptions.AllowWrites) throw new InvalidOperationException("FHIR writes are disabled by configuration.");
        if (confirmation != "I understand this modifies HAPI data")
            throw new ArgumentException("The explicit X-FHIRIL-Confirm-Write header is required.");
        if (_oauthOptions.Mode.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FHIR writes are disabled in anonymous authentication mode.");
    }

    private static bool VersionMatches(string? header, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(versionId)) return false;
        var supplied = header.Trim();
        if (supplied.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) supplied = supplied[2..];
        return supplied.Trim('"') == versionId;
    }

    private ContentResult Fhir(Resource resource) => Content(FhirContent(resource), "application/fhir+json");
    private static string FhirContent(Resource resource) => new FhirJsonSerializer().SerializeToString(resource);
}
