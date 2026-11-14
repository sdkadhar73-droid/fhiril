namespace FhirIl.Api.Configuration;

public sealed class OAuthOptions
{
    public const string SectionName = "OAuth";
    public string Mode { get; set; } = "Anonymous";
    public string? Authority { get; set; }
    public string? ApiAudience { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? SigningKeyPath { get; set; }
    public string? SigningKeyId { get; set; }
    public string[] Scopes { get; set; } = ["patient/Patient.read"];
    public string[] ApiAllowedScopes { get; set; } = [];
}
