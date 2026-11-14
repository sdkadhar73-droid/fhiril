namespace FhirIl.Api.Configuration;

public sealed class LocalOAuthOptions
{
    public const string SectionName = "LocalOAuth";
    public bool Enabled { get; set; } = false;
    public string Issuer { get; set; } = "https://localhost:5265/";
    public string Audience { get; set; } = "fhiril-api";
    public string ClientId { get; set; } = "fhiril-web";
    public string ClientSecret { get; set; } = "change-me";
    public string RedirectUri { get; set; } = "https://localhost:5265/signin-oidc";
    public string MachineClientId { get; set; } = "fhiril-service";
    public string MachineClientSecret { get; set; } = "change-me";
    public string DemoUsername { get; set; } = "demo";
    public string DemoPassword { get; set; } = "change-me";
    public string[] Scopes { get; set; } = ["patient/Patient.read", "patient/Encounter.read", "patient/Observation.read"];
}
