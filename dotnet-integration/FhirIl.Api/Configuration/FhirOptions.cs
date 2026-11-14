namespace FhirIl.Api.Configuration;

public sealed class FhirOptions
{
    public const string SectionName = "LocalHapi";
    public string BaseUrl { get; set; } = "http://localhost:8080/fhir";
    public int TimeoutSeconds { get; set; } = 15;
    public bool AllowWrites { get; set; }
}
