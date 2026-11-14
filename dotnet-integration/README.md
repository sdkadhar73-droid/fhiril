# FHIRIL .NET FHIR R4 Integration

ASP.NET Core Web API targeting .NET 10, using Firely FHIR R4 SDK 6.8.0 to communicate with the assessment HAPI FHIR server. The application keeps OAuth/token handling outside the FHIR service layer.

## Project layout

- `FhirIl.Api/` — controllers, FHIR application service, configuration, OAuth providers, Firely gateway, error middleware and server discovery.
- `FhirIl.UnitTests/` — isolated unit tests for token behavior, scopes and retry safety.
- `FhirIl.IntegrationTests/` — opt-in read-only checks against the configured HAPI endpoint.
- `FhirIl.Integration.slnx` — solution.

## Prerequisites and run

- .NET SDK 10.0 (verified locally as 10.0.403).
- Network access to NuGet for restore.
- HAPI FHIR R4 available at `http://localhost:8080/fhir` for integration checks.

From this directory in PowerShell:

```powershell
Copy-Item .env.example .env
dotnet restore .\FhirIl.Integration.slnx
dotnet build .\FhirIl.Integration.slnx
dotnet test .\FhirIl.UnitTests\FhirIl.UnitTests.csproj
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
dotnet run --project .\FhirIl.Api\FhirIl.Api.csproj
```

The `.env` file is an example for reference; ASP.NET Core does not load `.env` automatically. Set equivalent environment variables in PowerShell or use User Secrets for local secrets. Local HAPI configuration uses `LocalHapi__BaseUrl`; OAuth settings use `OAuth__Mode`; sandbox settings use `SmartSandbox__*`. The API binds only to loopback in the documented launch profile.

Swagger UI is available at `http://127.0.0.1:5080/swagger` in Development. Health endpoints are `/api/health` and `/api/health/fhir`; capability and SMART discovery details are at `/api/fhir/capabilities`.

## API endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/health` | Process health |
| GET | `/api/health/fhir` | HAPI metadata connectivity and R4 check |
| GET | `/api/fhir/capabilities` | CapabilityStatement summary and SMART discovery |
| GET | `/api/fhir/patients?identifier=system%7Cvalue&name=...&count=20` | Patient search |
| GET | `/api/fhir/patients/{id}` | Patient read |
| POST | `/api/fhir/patients` | Explicit Patient create; disabled unless configured and confirmed |
| PUT | `/api/fhir/patients/{id}` | Patient update with current `If-Match` version and explicit confirmation |
| DELETE | `/api/fhir/patients/{id}` | Patient delete with current `If-Match` version and explicit confirmation |
| GET | `/api/fhir/encounters?patient={id}` | Encounter search |
| GET | `/api/fhir/encounters/{id}` | Encounter read |
| GET | `/api/fhir/observations?patient={id}` | Observation search |
| GET | `/api/fhir/observations/{id}` | Observation read |
| GET | `/api/auth/login?iss={fhirBase}&launch={opaqueLaunch}` | Interactive SMART/OIDC launch when configured |
| POST | `/api/auth/logout` | Local and OIDC sign-out |

FHIR endpoints return `application/fhir+json`. Patient writes require `Fhir:AllowWrites=true`, an authenticated non-anonymous OAuth mode, the `patient/Patient.write` scope and `X-FHIRIL-Confirm-Write: I understand this modifies HAPI data`. Updates and deletes also require `If-Match: W/"{currentVersionId}"`. Do not enable writes for the current assessment unless an approved write demonstration is intended.

Example read-only requests:

```http
GET http://127.0.0.1:5080/api/health/fhir
GET http://127.0.0.1:5080/api/fhir/patients/1
GET http://127.0.0.1:5080/api/fhir/patients?count=5
GET http://127.0.0.1:5080/api/fhir/encounters?count=5
GET http://127.0.0.1:5080/api/fhir/observations?count=5
GET http://127.0.0.1:5080/api/fhir/capabilities
```

The successful health response reported `connectivity: available`, `metadataStatus: 200`, `fhirVersion: 4.0.1`, and `smartStatus: not_advertised`. The Patient read returned a FHIR `Patient` with `id: "1"`; the Patient search returned Bundle total 1, Encounter total 0, and Observation total 0 during this run. `/api/fhir/capabilities` includes each advertised resource type, its interactions and operations, system interactions/operations, SMART discovery fields, and SMART-related CapabilityStatement extensions.

## Authentication and SMART configuration

Authentication mode is explicit; there is no fallback from an authenticated mode to anonymous requests.

- `Anonymous` — development-only mode for the existing unauthenticated HAPI instance. Outbound Firely requests carry no bearer token. API writes are blocked in this mode. Base and Production configuration use `Unconfigured`, so the app fails at startup unless an authentication mode is explicitly selected.
- `InteractiveOidc` — ASP.NET Core OpenID Connect authorization code flow with PKCE, framework state and nonce validation, secure callback handling, and tokens stored in the encrypted, HttpOnly authentication cookie. Refresh runs when a refresh token and configured token endpoint are available. Configure `OAuth:Authority`, `OAuth:ClientId`, requested least-privilege `OAuth:Scopes`, and `OAuth:TokenEndpoint` for refresh. SMART `aud` is set to the configured FHIR base; an EHR `iss` must match that configured base and opaque launch values are length-limited before forwarding.
- `ClientCredentials` — outgoing OAuth client credentials token acquisition. Configure token endpoint, client ID, secret through a secret provider, and requested scopes. The incoming Web API separately validates JWT bearer tokens against `OAuth:Authority` and `OAuth:ApiAudience`.
- `SmartBackendServices` — outgoing `client_credentials` with an RS256 JWT client assertion (`iss` and `sub` are the client ID, audience is the token endpoint, unique `jti`, five-minute expiry). The private key is read from a configured file path and never logged or committed. Incoming Web API bearer JWT validation remains separately configured.

Local HAPI SMART discovery is read from `{LocalHapi:BaseUrl}/.well-known/smart-configuration`. `/api/fhir/capabilities` reports `advertised`, `not_advertised`, `endpoint_unavailable`, or `invalid_response` and extracts advertised OAuth extensions from the CapabilityStatement. Discovery does not itself establish that a working authorization server exists.

Configure only the scopes needed for a workflow. For SMART App Launch v2 the narrow patient demographic read scope is `patient/Patient.r`; `.r` grants instance reads only. `patient/Patient.rs` would also permit search. Older v1 `patient/Patient.read` maps to read and search, so it is not as narrow. Standalone patient launch uses `launch/patient`. The demo does not request `openid`/`fhirUser` because it does not use user identity. Backend Services scopes are a separate system-level flow.

Current HAPI has not been configured with an authorization server. Therefore only anonymous development reads are verified; interactive SMART, client credentials and Backend Services require separate real server configuration and are not claimed as end-to-end SMART compliance. EHR launch `iss`/`launch` is accepted into the protected OIDC authorization request, but the returned launch context is not yet used to constrain subsequent patient queries. Scopes are fixed by API policies and the authorization server must return scope claims for authenticated API requests.

Never place secrets in appsettings or source control. Use environment-specific secret storage such as ASP.NET Core User Secrets for Development and a managed secret store in deployment. Production OIDC cookies require HTTPS and use Secure, HttpOnly and SameSite=Lax. Configure and persist an ASP.NET Core Data Protection key ring for multi-instance/production deployments. TLS validation is not bypassed.

## Logging, errors and resilience

JSON console logs include operation name, duration, HTTP result summary, exception type, and correlation ID. They omit FHIR payloads, patient identifiers, request headers, tokens and exception messages. Every response includes `X-Correlation-ID`. API failures return ProblemDetails; FHIR OperationOutcome handling exposes issue codes only. Safe retries apply only to GET/HEAD for network failures, 429 and 5xx responses, honor `Retry-After`, and cap each delay at ten seconds. POST, PUT and DELETE are never automatically retried. Cancellation tokens are passed through Firely calls.

## Tests and existing synthetic Patient

Unit tests use local fakes and do not call HAPI or mutate resources. The integration test verifies metadata FHIR version, searches Patient/Encounter/Observation and reads the existing synthetic Patient `Patient/1`, asserting the assessment identifier without printing it. It never creates, updates or deletes FHIR records.

Run the read-only test with:

```powershell
$env:FHIRIL_FHIR_BASE_URL = 'http://localhost:8080/fhir'
dotnet test .\FhirIl.IntegrationTests\FhirIl.IntegrationTests.csproj
```

SMART availability is checked by the same test. If a compatible authorization server is not configured, OAuth flow tests can only exercise the isolated token/assertion construction code; they cannot prove end-to-end SMART access against HAPI.

## Current implementation limits

- No authorization server is deployed or configured; current HAPI accepts anonymous calls.
- No end-to-end OIDC, OAuth token, refresh-token or Backend Services exchange can be verified against current HAPI.
- SMART launch context is safely forwarded but is not yet applied as an authorization constraint on FHIR queries.
- Interactive OIDC tokens are in the protected authentication cookie in this POC. A production deployment should move authentication tickets to a server-side distributed ticket store and configure a persistent Data Protection key ring.
- Profile/IG validation and audit-event persistence are not configured.
- Write endpoints are present but disabled by default and were not executed against HAPI.
- Integration checks intentionally leave the existing HAPI resource inventory unchanged.

## SMART Health IT Sandbox (Provider Standalone, R4)

The sandbox connection is separate from local HAPI. `LocalHapi:BaseUrl` remains `http://localhost:8080/fhir` and uses the existing explicit `OAuth:Mode=Anonymous` development setting. The sandbox has its own `SmartSandbox` configuration, an independent OAuth scheme, a server-side in-memory token session store, and a Firely R4 client that blocks bearer-token requests outside the configured sandbox FHIR base URL. The sandbox token is never passed to the local HAPI gateway, and local HAPI requests do not use the sandbox session.

The selected launcher configuration uses this verified FHIR R4 issuer and its instance-specific discovery document:

```text
Issuer: https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/fhir
Discovery: {issuer}/.well-known/smart-configuration
Authorization: https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/auth/authorize
Token: https://launch.smarthealthit.org/v/r4/sim/WzIsIiIsIiIsIkFVVE8iLDAsMCwwLCIiLCIiLCIiLCIiLCIiLCIiLCIiLDEsMSwiIl0/auth/token
Client ID: fhiril-smart-demo (public client; no client secret)
Callback: http://localhost:5265/smart/callback
Scopes: launch/patient patient/Patient.r
```

The application re-fetches SMART discovery at `/smart/status` and before launch. It requires exact issuer and endpoint matches, HTTPS, FHIR R4, `authorization_code`, PKCE `S256`, public-client support, standalone patient context, and patient permission. The live CapabilityStatement reports FHIR version `4.0.0`; local HAPI reports `4.0.1`. The live SMART discovery document advertises `launch/patient`, `patient/*.*`, and `permission-v2`, but does not enumerate `patient/Patient.r`; the app uses this narrower SMART v2 scope based on the server's advertised v2 permission capability. It never requests the wildcard. The callback URI is fixed to the HTTP loopback launch profile and must match exactly in the launcher's redirect URI setting. Do not use this callback on a public host. Production requires HTTPS and a separately reviewed deployment configuration.

The public-client specialization of ASP.NET Core's OAuth middleware retains the framework's protected state/correlation and `UsePkce=true` S256 implementation, while omitting `client_secret` from token exchange. It requests only `launch/patient patient/Patient.r`. It does not request `openid` or `fhirUser` because it does not need end-user identity and does not validate ID tokens. The token response must include a valid `patient` context, a usable access token, expiry, and the exact Patient read scope; if any are missing, the callback fails closed. Tokens are retained only in process memory, not in browser-visible URLs, response bodies, logs, or authentication cookies. The browser cookie is HttpOnly, SameSite=Lax, and Secure in non-Development environments. Development uses ephemeral Data Protection keys; Production requires persistent key management. Sessions expire with the token or after the cookie's 20-minute limit; there is no refresh-token scope or refresh flow.

Run the existing app from `dotnet-integration` using the `http` launch profile:

```powershell
dotnet run --project .\FhirIl.Api\FhirIl.Api.csproj --launch-profile http
```

Then use these local routes:

```text
http://localhost:5265/smart          Launch page
http://localhost:5265/smart/status   Live discovery and readiness
http://localhost:5265/smart/launch   Start standalone authorization
http://localhost:5265/smart/session  Safe session and patient-context summary
http://localhost:5265/smart/patient  Read only the authorized Patient
```

The app profile sets the callback URI to `http://localhost:5265/smart/callback`. Configure that exact callback in the launcher's public-client settings if the launcher requests it. No patient ID is selected by the app: it reads the `patient` context returned by the token endpoint and allows only that Patient. `GET /smart/patient/{id}` is also available to demonstrate that a different ID is rejected.

**Discovery observation:** the sandbox discovery includes `client-public` but its `token_endpoint_auth_methods_supported` lists only `client_secret_basic`, `client_secret_post`, and `private_key_jwt`; there is no `none` entry. The code follows the selected public-client configuration and sends no client secret. The actual token exchange must confirm that the launcher accepts this combination; failures are surfaced and are not retried as a confidential client.

**Verification status:** the mock-based OAuth/security tests and local HAPI integration test are separate from live authorization. The `/smart/status` readiness route performs a live discovery check. Do not report the sandbox authorization or Patient read as successful until a browser launch, token exchange, patient-context check, and authenticated read complete. The SMART sandbox request is read-only. The local HAPI integration test remains separate and uses `LocalHapi` configuration.

## Verification results (2026-11-11)

- NuGet restore succeeded for all three projects. Firely package: `Hl7.Fhir.R4` 6.8.0; ASP.NET OpenID Connect/JWT/TestHost packages 10.0.14; IdentityModel token package 8.19.2; Swagger 10.0.1. Test package versions are pinned in each project file.
- `dotnet build FhirIl.Integration.slnx --no-restore`: succeeded, zero warnings and errors.
- Unit tests: 23 passed, 0 failed. Coverage includes anonymous/no-token behavior, missing OAuth config fail-closed, client-credentials token acquisition/cache, cryptographically verified Backend Services assertion, incoming valid/missing/invalid/expired bearer tokens, insufficient scope, bearer attachment, blocked writes, sanitized ProblemDetails/OperationOutcome, mapped upstream error statuses, GET transient retries and Retry-After handling, and no POST retry.
- Read-only HAPI integration tests: 1 passed. Metadata reported R4 `4.0.1`; Firely read `Patient/1` and matched the expected synthetic identifier; Patient, Encounter and Observation searches succeeded; SMART discovery returned `not_advertised`.
- Local API process: started on `127.0.0.1:5080` and then stopped. `/api/health` returned healthy, `/api/health/fhir` reported R4/HTTP 200, Swagger returned HTTP 200, `Patient/1` returned type Patient and matching ID/identifier, and searches returned Bundle totals Patient 1, Encounter 0, Observation 0.
- Capability details: the live CapabilityStatement contained 146 resource types and 7 system-level operations; Patient, Encounter and Observation advertised create/read/update/delete plus additional interactions.
- FHIR writes, eCR workflows and OAuth exchanges against an external authorization server were not run.

## Phase 4 checklist

- [x] .NET 10 Web API with layered controllers, application service, Firely FHIR infrastructure, configuration and dependency injection.
- [x] Typed Patient/Encounter/Observation search and read operations; guarded Patient create/update/delete APIs.
- [x] FHIR metadata, R4 version and capability discovery; SMART discovery reports not advertised by current HAPI.
- [x] Explicit anonymous development mode and OAuth modes for OIDC authorization code with PKCE, client credentials and SMART Backend Services assertions.
- [x] Bearer-token forwarding, expiry tracking, refresh when available, scope policies and no authenticated-to-anonymous fallback.
- [x] Structured PHI-conscious logs, correlation IDs, health routes, ProblemDetails, OperationOutcome issue-code handling, cancellation and safe read retries.
- [x] Unit and read-only HAPI integration test suites pass.
- [ ] End-to-end SMART/OAuth login and token exchange against an external authorization server; none is configured for this assessment.
- [ ] SMART EHR launch context enforcement on patient/resource queries; launch is forwarded securely but not yet used to constrain data access.
- [ ] FHIR profile/implementation-guide validation, persistent audit trail, distributed authentication ticket storage and production Data Protection key management.
