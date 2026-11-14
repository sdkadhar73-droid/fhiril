# HAPI FHIR R4

## Verified local server

| Item | Observed value |
| --- | --- |
| Base URL | `http://localhost:8080/fhir` |
| HAPI server | 7.6.0 |
| FHIR release | R4 (`4.0.1`) |
| CapabilityStatement status | `active` |
| CapabilityStatement kind | `instance` |
| CapabilityStatement date | `2024-11-22` |
| Advertised resource types | 146 |

These values were read from `GET /metadata`; they describe the running local instance at the time of inspection. They do not establish conformance to an implementation guide such as US Core or UDS+.

## CapabilityStatement findings

Request:

```http
GET http://localhost:8080/fhir/metadata
Accept: application/fhir+json
```

Observed response: `200 OK`, `application/fhir+json`, with a `CapabilityStatement` reporting FHIR `4.0.1` and HAPI `7.6.0`.

The 146 advertised resource types are:

```text
Account, ActivityDefinition, AdverseEvent, AllergyIntolerance, Appointment,
AppointmentResponse, AuditEvent, Basic, Binary, BiologicallyDerivedProduct,
BodyStructure, Bundle, CapabilityStatement, CarePlan, CareTeam, CatalogEntry,
ChargeItem, ChargeItemDefinition, Claim, ClaimResponse, ClinicalImpression,
CodeSystem, Communication, CommunicationRequest, CompartmentDefinition,
Composition, ConceptMap, Condition, Consent, Contract, Coverage,
CoverageEligibilityRequest, CoverageEligibilityResponse, DetectedIssue, Device,
DeviceDefinition, DeviceMetric, DeviceRequest, DeviceUseStatement,
DiagnosticReport, DocumentManifest, DocumentReference, EffectEvidenceSynthesis,
Encounter, Endpoint, EnrollmentRequest, EnrollmentResponse, EpisodeOfCare,
EventDefinition, Evidence, EvidenceVariable, ExampleScenario,
ExplanationOfBenefit, FamilyMemberHistory, Flag, Goal, GraphDefinition, Group,
GuidanceResponse, HealthcareService, ImagingStudy, Immunization,
ImmunizationEvaluation, ImmunizationRecommendation, ImplementationGuide,
InsurancePlan, Invoice, Library, Linkage, List, Location, Measure, MeasureReport,
Media, Medication, MedicationAdministration, MedicationDispense,
MedicationKnowledge, MedicationRequest, MedicationStatement, MedicinalProduct,
MedicinalProductAuthorization, MedicinalProductContraindication,
MedicinalProductIndication, MedicinalProductIngredient,
MedicinalProductInteraction, MedicinalProductManufactured,
MedicinalProductPackaged, MedicinalProductPharmaceutical,
MedicinalProductUndesirableEffect, MessageDefinition, MessageHeader,
MolecularSequence, NamingSystem, NutritionOrder, Observation,
ObservationDefinition, OperationDefinition, OperationOutcome, Organization,
OrganizationAffiliation, Parameters, Patient, PaymentNotice,
PaymentReconciliation, Person, PlanDefinition, Practitioner, PractitionerRole,
Procedure, Provenance, Questionnaire, QuestionnaireResponse, RelatedPerson,
RequestGroup, ResearchDefinition, ResearchElementDefinition, ResearchStudy,
ResearchSubject, RiskAssessment, RiskEvidenceSynthesis, Schedule, SearchParameter,
ServiceRequest, Slot, Specimen, SpecimenDefinition, StructureDefinition,
StructureMap, Subscription, Substance, SubstanceNucleicAcid, SubstancePolymer,
SubstanceProtein, SubstanceReferenceInformation, SubstanceSourceMaterial,
SubstanceSpecification, SupplyDelivery, SupplyRequest, Task,
TerminologyCapabilities, TestReport, TestScript, ValueSet, VerificationResult,
VisionPrescription
```

### Resource interactions

Every advertised resource except `OperationDefinition` reports these interactions:

| Interaction code | Meaning |
| --- | --- |
| `create` | Create a resource with POST |
| `read` | Read a resource by ID |
| `update` | Update a resource with PUT |
| `delete` | Delete a resource by ID |
| `search-type` | Search resources of that type |
| `vread` | Read a specific version |
| `patch` | Partially update a resource |
| `history-type` | Read history for a resource type |
| `history-instance` | Read history for one resource |

`OperationDefinition` advertises the same set except `update` and `delete`. The interactions above are advertised by the server; they do not by themselves prove that every request succeeds under every configuration or access policy.

Patient, Encounter, and Observation advertise these resource-level operations:

- `$validate`, `$meta`, `$meta-add`, `$meta-delete`, `$expunge`
- Patient and Encounter also advertise `$everything`.
- Observation also advertises `$lastn`.

The CapabilityStatement advertises these system-level operations:

`$reindex`, `$perform-reindexing-pass`, `$meta`, `$mark-all-resources-for-reindexing`, `$get-resource-counts`, `$expunge`, and `$reindex-terminology`.

Other resource-level operations are available for some resource types, including terminology operations on `CodeSystem`, `ConceptMap`, and `ValueSet`; `$document` on `Composition`; `$snapshot` on `StructureDefinition`; and `$trigger-subscription` on `Subscription`. Consult the live CapabilityStatement for exact resource/operation pairings and definitions before relying on an operation.

## Authentication observation

The inspected CapabilityStatement did not contain a `rest.security` declaration. An unauthenticated `GET /Patient?_count=1` returned `200 OK` with a searchset Bundle and `total: 0`. This demonstrates that this read/search request was accepted without a bearer token. It does not prove that all resource interactions or operations are unauthenticated, nor does absence of the declaration alone establish the server's full security configuration. No authentication settings were changed.

## Harmless read demonstration

```http
GET http://localhost:8080/fhir/Patient?_count=1
Accept: application/fhir+json
```

Observed: `200 OK`, a FHIR `Bundle` with `type: searchset` and `total: 0` at the time of the check. No Patient data was returned.

The same read-only search was performed for `GET /Encounter?_count=1` and `GET /Observation?_count=1`; both returned `200 OK` with a Bundle and `total: 0`.

## Synthetic Patient CRUD demonstration

The following sequence was executed after explicit approval. The identifier system and value are synthetic and assessment-specific. The identifier was searched before creation and had zero matches.

| Step | Request/result | Verified outcome |
| --- | --- | --- |
| Collision check | `GET /Patient?identifier={system}\|{value}` → `200` | `Bundle.total` was `0` |
| Create | `POST /Patient` → `201` | Server assigned ID `1`, version `1`; response `Location` was `/fhir/Patient/1/_history/1` |
| Read | `GET /Patient/1` → `200` | Synthetic Patient returned, family `Example`, version `1` |
| Update | `PUT /Patient/1` → `200` | Family changed to `ExampleUpdated`, version advanced to `2` |
| Verify update | `GET /Patient/1` → `200` | Family `ExampleUpdated`, version `2` |
| Delete | `DELETE /Patient/1` → `200` | OperationOutcome reported `SUCCESSFUL_DELETE` |
| Verify deletion by ID | `GET /Patient/1` → `410 Gone` | HAPI reported the resource as deleted |
| Verify deletion by identifier | Search → `200` | `Bundle.total` was `0` |

HAPI returns `410 Gone` for a direct read of this deleted resource, rather than `404 Not Found`. The identifier search confirmed that no matching live resource remained. The Patient contained no real or identifying patient information. These results verify basic CRUD behavior on this local server; they do not establish SMART on FHIR security or implementation-guide conformance.

## Synthetic Encounter and Observation CRUD demonstrations

Both identifiers were searched before creation and returned `200 OK` with `Bundle.total: 0`. The requests below were sent without an `Authorization` header. The resources were standalone synthetic examples and were not linked to a Patient.

### Encounter

Create request (`POST /Encounter`, `Content-Type: application/fhir+json`):

```json
{
  "resourceType": "Encounter",
  "identifier": [
    {
      "system": "https://fhiril.example.org/synthetic-resources",
      "value": "encounter-demo-20261106-001"
    }
  ],
  "status": "planned",
  "class": {
    "system": "http://terminology.hl7.org/CodeSystem/v3-ActCode",
    "code": "AMB",
    "display": "ambulatory"
  },
  "period": { "start": "2026-11-06T09:00:00Z" }
}
```

| Request | Observed response |
| --- | --- |
| `POST /Encounter` | `201 Created`; response resource ID `2`, version `1` |
| `GET /Encounter/2` | `200 OK`; identifier matched, status `planned`, version `1` |
| `PUT /Encounter/2` with status changed to `in-progress` | `200 OK`; response version `2` |
| `GET /Encounter/2` | `200 OK`; status `in-progress`, version `2` |
| `DELETE /Encounter/2` | `200 OK`; OperationOutcome code `SUCCESSFUL_DELETE` |
| `GET /Encounter/2` after delete | `410 Gone` |
| Search by the synthetic identifier after delete | `200 OK`; `Bundle.total: 0` |

### Observation

Create request (`POST /Observation`, `Content-Type: application/fhir+json`):

```json
{
  "resourceType": "Observation",
  "identifier": [
    {
      "system": "https://fhiril.example.org/synthetic-resources",
      "value": "observation-demo-20261106-001"
    }
  ],
  "status": "final",
  "category": [
    {
      "coding": [
        {
          "system": "http://terminology.hl7.org/CodeSystem/observation-category",
          "code": "vital-signs",
          "display": "Vital Signs"
        }
      ]
    }
  ],
  "code": {
    "coding": [
      {
        "system": "http://loinc.org",
        "code": "8310-5",
        "display": "Body temperature"
      }
    ]
  },
  "effectiveDateTime": "2026-11-06T09:00:00Z",
  "valueQuantity": {
    "value": 37.0,
    "unit": "Cel",
    "system": "http://unitsofmeasure.org",
    "code": "Cel"
  }
}
```

| Request | Observed response |
| --- | --- |
| `POST /Observation` | `201 Created`; response resource ID `3`, version `1` |
| `GET /Observation/3` | `200 OK`; identifier matched, value `37.0 Cel`, version `1` |
| `PUT /Observation/3` with value changed to `37.1 Cel` | `200 OK`; response version `2` |
| `GET /Observation/3` | `200 OK`; value `37.1 Cel`, version `2` |
| `DELETE /Observation/3` | `200 OK`; OperationOutcome code `SUCCESSFUL_DELETE` |
| `GET /Observation/3` after delete | `410 Gone` |
| Search by the synthetic identifier after delete | `200 OK`; `Bundle.total: 0` |

HAPI uses `410 Gone` for direct reads of these deleted IDs. Identifier searches verified that neither deleted resource remained as a live search result.

## Security finding: unauthenticated writes accepted

The Encounter and Observation POST, PUT, and DELETE requests above were sent without an Authorization header and succeeded (`201` for create and `200` for update/delete). Combined with the earlier unauthenticated metadata and search responses, this is direct evidence that this local instance accepts unauthenticated access to these interactions. The CapabilityStatement also omits `rest.security`.

Treat the current instance as an unauthenticated development server. Do not expose it beyond a trusted local environment or use real patient data. No server configuration was changed as part of this assessment. Authentication behavior for other resource types and server entry points has not been assessed. SMART on FHIR or OAuth protection has not been demonstrated.

## Assessment checklist

- [x] Confirm HAPI FHIR R4 metadata and server version.
- [x] Inspect advertised resource types, interactions, and operations.
- [x] Observe unauthenticated metadata and Patient search behavior.
- [x] Verify harmless Patient search.
- [x] Verify harmless Encounter and Observation searches.
- [x] Demonstrate synthetic Patient create, read, update, and delete with response verification.
- [x] Demonstrate synthetic Encounter and Observation create, read, update, and delete with version and deletion verification.
- [ ] Capture reusable request/response artifacts and expand troubleshooting instructions.
- [x] Confirm unauthenticated create, update, and delete on the local server; report as a security finding.
- [ ] Assess security configuration beyond observed unauthenticated access behavior.
- [ ] Run applicable Inferno tests after selecting the assessment suite.
