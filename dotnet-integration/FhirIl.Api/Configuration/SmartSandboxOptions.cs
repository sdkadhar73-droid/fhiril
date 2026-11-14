namespace FhirIl.Api.Configuration;

public sealed class SmartSandboxOptions
{
    public const string SectionName = "SmartSandbox";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string CallbackPath { get; set; } = "/smart/callback";
    public string[] Scopes { get; set; } = ["launch/patient", "patient/Patient.read"];
    public int TimeoutSeconds { get; set; } = 15;
}
