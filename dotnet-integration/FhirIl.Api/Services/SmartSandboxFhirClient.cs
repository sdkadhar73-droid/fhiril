using FhirIl.Api.Authentication;
using FhirIl.Api.Configuration;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Services;

public sealed class SmartSandboxFhirClient(IOptions<SmartSandboxOptions> options)
{
    private readonly SmartSandboxOptions _options = options.Value;

    public async Task<Patient> ReadAuthorizedPatientAsync(SmartSandboxSession session, string? requestedId, CancellationToken cancellationToken)
    {
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("The SMART Sandbox session has expired.");
        if (!session.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("patient/Patient.r", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The SMART Sandbox session does not grant Patient read access.");
        if (!string.IsNullOrWhiteSpace(requestedId) && !string.Equals(requestedId, session.PatientId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The requested Patient is outside the SMART launch context.");

        var issuer = SmartSandboxIssuer.Validate(session.Issuer);
        if (!string.Equals(issuer.ToString().TrimEnd('/'), _options.Issuer.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException("The SMART session issuer no longer matches the configured sandbox issuer.");
        var handler = new SandboxBearerHandler(issuer, session.AccessToken) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } };
        var client = new FhirClient(issuer, new FhirClientSettings
        {
            PreferredFormat = ResourceFormat.Json,
            Timeout = Math.Clamp(_options.TimeoutSeconds, 1, 120) * 1000,
            VerifyFhirVersion = true
        }, handler);
        var patientUri = new Uri(issuer, $"Patient/{Uri.EscapeDataString(session.PatientId)}");
        var patient = await client.ReadAsync<Patient>(patientUri, ct: cancellationToken);
        if (patient is null || patient.Id != session.PatientId)
            throw new InvalidOperationException("The SMART Sandbox returned a Patient that did not match the authorized context.");
        return patient;
    }
}

public sealed class SandboxBearerHandler(Uri issuer, string accessToken) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !issuer.IsBaseOf(request.RequestUri))
            throw new InvalidOperationException("Sandbox bearer token blocked for a request outside the sandbox FHIR base URL.");
        if (request.Headers.Authorization is not null)
            throw new InvalidOperationException("A sandbox request already contained an authorization header.");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return base.SendAsync(request, cancellationToken);
    }
}
