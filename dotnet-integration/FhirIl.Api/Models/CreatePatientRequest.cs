using System.ComponentModel.DataAnnotations;

namespace FhirIl.Api.Models;

public sealed class CreatePatientRequest
{
    [Required, StringLength(120)] public string FamilyName { get; init; } = "";
    [Required, StringLength(120)] public string GivenName { get; init; } = "";
    [StringLength(200)] public string? IdentifierSystem { get; init; }
    [StringLength(200)] public string? IdentifierValue { get; init; }
}
