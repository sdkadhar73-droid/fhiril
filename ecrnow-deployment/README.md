# eCRNow assessment Docker deployment

This directory contains an assessment-specific Docker build and Compose definition. It leaves the separate eCRNow repository, including its upstream `Dockerfile` and `docker-compose.yml`, unchanged. It reuses the existing HAPI container; it does not define a HAPI service.

## Deployment record

Deployment was performed on 2026-11-08. PostgreSQL is running and healthy. eCRNow is running, responds with HTTP 200 on its local root endpoint, and is published only at `127.0.0.1:8081`.

From inside the eCRNow container, HAPI `/metadata` returned HTTP 200, `CapabilityStatement`, software version `7.6.0`, and FHIR version `4.0.1`. Read-only `_summary=count` searches returned HTTP 200 and total 0 for Patient, Encounter and Observation. No FHIR resources were created or changed.

The `docker compose up --build -d` command returned a Docker name conflict for `ecrnow-deployment-ecrnow-db-1`; a subsequent state check showed both eCRNow and PostgreSQL containers running, with PostgreSQL healthy. No restart or cleanup was performed. Inspect container ownership before future Compose changes to avoid colliding with existing containers.

## Intended topology

| Service | Container/service name | Network(s) | Host exposure | Storage |
|---|---|---|---|---|
| Existing HAPI | `onix-hapi-1` (image `hapiproject/hapi:v7.6.0`) | Existing `onix_default` and `fhiril-ecrnow-hapi` (alias `hapi`) | Existing port 8080 mapping remains unchanged | Existing H2 configuration; no persistent mount was observed |
| eCRNow | `ecrnow-app` | `fhiril-ecrnow-hapi` and internal `fhiril-ecrnow-backend` | `127.0.0.1:8081` to container port 8080 | Read-only configuration bind mount and named output volume |
| PostgreSQL | `ecrnow-db` | Internal `fhiril-ecrnow-backend` only | No host port | Named volume `fhiril-ecrnow-postgres-data` |

The application resolves HAPI at `http://hapi:8080/fhir` after the existing HAPI container is attached to `fhiril-ecrnow-hapi` with alias `hapi`. The Compose file expects that network to exist and will not create it because it is declared external. PostgreSQL is reachable by the Compose service name `ecrnow-db` at port 5432 from the internal backend network.

## Configuration decisions and known gaps

- The upstream Compose file configured one PostgreSQL database name but used a different name in the JDBC URL, and its database password did not match the application's JDBC password. This prepared Compose definition derives both services' database settings from the same local `.env` values.
- Upstream `application.properties` sets `server.port=8081`, while its Compose file maps host port 8081 to container port 8080. This definition explicitly overrides `SERVER_PORT=8080` and publishes only on Windows loopback at `127.0.0.1:8081`.
- Upstream Compose expects an external network that it does not create. This deployment uses a separately named external HAPI application network plus an internal app/database network.
- The upstream Dockerfile uses an early access Java 24 image while the POM sets Java 17. The assessment-only Dockerfile uses Java 17 for both Maven build and runtime. Its build command skips tests (`-Dmaven.test.skip=true`), so a successful image build would not prove tests passed.
- `SECURITY_KEY` is required from local environment configuration and is never given a committed value. The sample is only a placeholder, not a usable key.
- eCRNow's `eicr.fhir.server.url` setting is for eICR FHIR validation; it is not verified as the EHR FHIR base URL. eCRNow associates the EHR FHIR base URL with healthcare-setting data. Therefore this Compose file does not invent an unsupported `FHIR_BASE_URL` environment property. Before a FHIR integration demonstration, the correct healthcare-setting record and its SMART/client configuration must be reviewed and configured through the supported eCRNow mechanism. The intended base URL is `http://hapi:8080/fhir`.
- HAPI previously accepted unauthenticated FHIR requests and is published on all host interfaces. Joining a private Docker network does not remove that existing host port mapping or add authentication. This deployment does not claim production security or SMART on FHIR authorization.
- The asset bind mounts point to existing files/directories in the separate eCRNow checkout and are read-only. The checkout does not contain the default root-level `eRSDv2_specification_bundle.json`; the prepared environment uses the available versioned `documents/app-artifacts/kars/eRSDv2_specification_bundle 12_12_2022.json` candidate. Confirm that this version is suitable for the assessment before relying on trigger behavior. Generated BSA output is stored separately in a named volume.
- The PostgreSQL image is pinned to major version 15, matching the upstream Compose major version. The named database volume persists across container replacement, but it is not a backup; export/backup procedures remain to be defined.

## Deployment commands used

1. Copy `.env.example` to `.env` in this directory and replace all placeholders with local values. Do not commit `.env`.
2. Confirm the source and asset paths in `.env` resolve to the existing `D:\syedkadhar\POC\onix\eCRNow\eCRNow` checkout and referenced assets.
3. Create the dedicated HAPI network and connect only the existing container:

   ```powershell
   docker network create fhiril-ecrnow-hapi
   docker network connect --alias hapi fhiril-ecrnow-hapi onix-hapi-1
   ```

4. Run from this directory:

   ```powershell
   docker compose --env-file .env -f compose.yaml config --quiet
   docker compose --env-file .env -f compose.yaml up --build -d
   ```

The first command validates the resolved Compose configuration. The second builds eCRNow and starts only `ecrnow-app` and `ecrnow-db` from this file; there is no HAPI service definition. HAPI connectivity was verified separately from inside the eCRNow container.

## HealthcareSetting and application verification (2026-11-08)

- The pre-create check returned HTTP 200 with `exists=false` for `http://hapi:8080/fhir`.
- HealthcareSetting creation returned HTTP 200 and generated ID `2`. A subsequent GET by ID also returned HTTP 200. Verified safe fields: FHIR base URL `http://hapi:8080/fhir`, FHIR version `R4`, authentication type `Unknown`, and FHIR API enabled. Secret/token fields were not displayed.
- `Unknown` uses eCRNow's existing empty-token fallback and is temporary for this unauthenticated development HAPI. It is not a dedicated NoAuth mode and must not be treated as a production authentication design.
- With approval, a minimal read-only diagnostic route was added to the separate eCRNow checkout at `GET /api/diagnostics/hapi-fhir`. It loads HealthcareSetting ID 2 and uses eCRNow's `FhirContextInitializer` and HAPI FHIR client without acquiring a token, persisting token state, returning resource contents, or invoking workflow/trigger code. It performs only CapabilityStatement and `_summary=count` reads for Patient, Encounter and Observation. This is an assessment-only development diagnostic, not a production endpoint.
- Application-level diagnostic GET returned outer HTTP 200. Metadata retrieval succeeded with HTTP 200, `CapabilityStatement`, HAPI software version `7.6.0`, and FHIR version `4.0.1` (serialized by PowerShell as enum label `_4_0_1`). The response reported `bearerTokenSent=false`, `healthcareSettingTokenStateWritten=false`, and `fhirWritesPerformed=false`.
- Patient, Encounter and Observation count searches each failed within eCRNow's FHIR-client call and returned the sanitized error `FHIR request failed: NullPointerException`; no HTTP status was available for those inner operations. Thus application-level metadata access is verified, but resource search is not. Although Phase 2.9's separate direct-container searches reported HTTP 200 and totals 0, those results do not substitute for successful eCRNow client searches.
- The previous comparison of HealthcareSetting ID 2 before and after the diagnostic showed token and expiry fields empty and unchanged. The diagnostic endpoint does not return those fields.

## Validation limitations

HealthcareSetting is configured. Application-level metadata access is verified, but resource searches currently fail with a sanitized `NullPointerException`; the cause must be diagnosed before claiming Patient or clinical data retrieval. The diagnostic route is read-only and avoids the existing service method's token/expiry database update. Do not run launch/workflow endpoints for this verification: they may evaluate triggers. Successful connectivity or metadata access does not prove trigger evaluation, eICR generation, or report routing.

## Phase 3.2 search fix and runtime verification (2026-11-09)

- Source inspection found that `ecr.fhir.retry.enabled=true` wraps the standard HAPI client in `EcrFhirRetryClient`. Its `EcrFhirRetryableSearch` implements `byUrl(...)` but explicitly throws `NotImplementedOperationException` from `summaryMode(...)`. The diagnostic's typed `.summaryMode(SummaryEnum.COUNT)` call is therefore incompatible with the retry wrapper. eCRNow's existing resource query code uses `search().byUrl(...).returnBundle(...).execute()`.
- The diagnostic now uses `byUrl("http://hapi:8080/fhir/{ResourceType}?_summary=count")` for Patient, Encounter and Observation. This retains FHIR count-only responses and avoids requesting or returning resource payloads. Unexpected search exceptions are logged with stack traces server-side; the HTTP response remains sanitized.
- Added focused test `ReadOnlyHapiDiagnosticControllerTest.searchCountUsesSupportedCountOnlyByUrlQuery`. Result: 1 test passed, 0 failures/errors/skips. Maven compiled the updated application at Java release 17. `mvn "-Dmaven.test.skip=true" package` completed with `BUILD SUCCESS`; tests were skipped for that package run because the focused test had already run separately.
- A subsequent `mvn -DskipTests package` unexpectedly proceeded into the full suite; it was interrupted after Spring Boot integration tests began. Do not interpret that run as a complete suite result.
- The updated app image built successfully with Docker Compose, then only `ecrnow-app` was recreated using `--no-deps --force-recreate`. The app became ready on `127.0.0.1:8081`; PostgreSQL remained healthy and `onix-hapi-1` remained running.
- After deployment, `GET /api/diagnostics/hapi-fhir` returned outer HTTP 200. Metadata returned HTTP 200 with `CapabilityStatement`, server version `7.6.0`, and FHIR R4 `4.0.1`. Patient, Encounter and Observation `_summary=count` searches each returned HTTP 200 and Bundle total 0. The endpoint reported no bearer token, no HealthcareSetting token-state write and no FHIR writes.
- A before/after read of HealthcareSetting ID 2 showed token/expiry-related fields unchanged. `ehrAccessToken` and `ehrAccessTokenExpirationTime` were empty; `ehrAccessTokenExpiryDuration` was populated and unchanged (value intentionally omitted). No authentication configuration or database data was modified.
- The earlier reported `NullPointerException` stack was not available: the prior controller returned only the exception class. Source inspection identified the unsupported `summaryMode(...)` call on the enabled retry wrapper; the replacement `byUrl(...)` count-only searches now succeed in the running app. The fix's live result is verified, but the precise stack for the prior failure cannot be reconstructed retroactively.
- Docker image build used the Dockerfile's `mvn clean package -Dmaven.test.skip=true`; tests were skipped in that container build. The focused local unit test passed separately (1 run, 0 failures/errors/skips). A separate full-suite package attempt was interrupted and is not a full-suite result.
- HAPI and PostgreSQL containers were not recreated or restarted. No FHIR resources were created, updated or deleted, and no eCR workflow, trigger evaluation, eICR generation or report routing was run.

## Phase 3.4 synthetic Patient read verification (2026-11-10)

- Searched HAPI by identifier before creation; the FHIR search Bundle returned HTTP 200 and total `0`.
- Created exactly one synthetic R4 Patient. HAPI returned HTTP 201 and assigned ID `1`. The identifier system is `https://fhiril.example.org/identifiers/ecrnow-readback`; its unique value is `FHIRIL-ECRNOW-READBACK-3a0890947002d14ea12193747a2454ea`. The synthetic name is given `FHIRIL`, family `Synthetic Test`.
- Direct HAPI `GET /Patient/1` returned HTTP 200, resource type `Patient`, ID `1`, and the expected synthetic identifier.
- Added `GET /api/diagnostics/hapi-fhir/patient/{patientId}` to the assessment-only diagnostic controller. It loads HealthcareSetting ID 2, uses the configured retryable FHIR client with an empty access token, performs a read-only `Patient/{id}` read, and returns only the resource type, ID, match result, and safe diagnostic flags.
- eCRNow `GET /api/diagnostics/hapi-fhir/patient/1` returned HTTP 200 with resource type `Patient`, ID `1`, and `idMatches=true`. It reported no bearer token, no token-state write, and no FHIR writes.
- Focused Maven tests: 2 run, 0 failures, 0 errors, 0 skipped. The Docker app image build succeeded. Only the eCRNow app container was recreated; PostgreSQL stayed healthy and the existing HAPI container remained running.
- HealthcareSetting token fields were compared in memory before and after the diagnostic read and were unchanged. The access token and expiry timestamp were empty; the expiry-duration field was populated and unchanged. No token/expiry values were printed.
- Patient count was `1` before and after the diagnostic read; Encounter and Observation counts were `0` before and after. No other FHIR resource was created or modified. No eCR workflow, trigger evaluation, eICR generation, or report routing was run.
