# FHIRIL - Healthcare Interoperability Assessment

## Components
- eCRNow: Electronic Case Reporting
- HAPI FHIR: FHIR R4 server
- .NET Integration: ASP.NET Core and Firely FHIR SDK (local HAPI read-only mode plus isolated SMART Sandbox Provider Standalone launch implementation; see `dotnet-integration/README.md` for live verification status)
- UDS+: UDS+ implementation analysis
- Inferno: FHIR conformance testing

## Project Structure
- dotnet-integration/
- hapi-fhir/
- uds-plus/
- inferno/
- docs/

## Architecture
- [.NET FHIR integration architecture](docs/architecture/dotnet-fhir-integration.md)

## Upstream Reference
https://github.com/drajer-health/eCRNow
