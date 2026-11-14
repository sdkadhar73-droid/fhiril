# .NET FHIR Integration Architecture

```mermaid
flowchart LR
    Caller[API client or browser]
    API[ASP.NET Core Web API]
    Auth[Authentication and scope policies]
    OIDC[OAuth 2.0 / SMART authorization server]
    Token[Token provider and protected authentication ticket/cache]
    App[FHIR application services]
    Firely[Firely FHIR R4 client]
    LocalHAPI[Local HAPI FHIR R4\nhttp://localhost:8080/fhir]
    Sandbox[SMART Health IT Sandbox\nFHIR R4 issuer]
    SandboxAuth[Sandbox Authorization Server]
    Discovery[Local HAPI capability and SMART discovery]
    SandboxDiscovery[Sandbox SMART discovery\nissuer, endpoints, grants, PKCE]
    Session[Server-side sandbox token session\npatient context + expiry]
    SandboxClient[Separate Firely R4 sandbox client\nexact-host bearer guard]

    Caller --> API
    API --> Auth
    Auth <-->|Existing configured OAuth modes| OIDC
    Auth --> App
    App --> Firely
    Token --> Firely
    Firely -->|FHIR R4 HTTP; anonymous in Development| LocalHAPI
    API --> Discovery
    Discovery --> LocalHAPI
    SandboxDiscovery --> Sandbox
    SandboxDiscovery -. validated authorization/token endpoints .-> SandboxAuth
    Caller -->|/smart/launch; Authorization Code + PKCE S256| SandboxAuth
    SandboxAuth -->|callback /smart/callback| API
    API --> Session
    Session --> SandboxClient
    SandboxClient -->|FHIR R4 Patient read; sandbox bearer only| Sandbox
```

The API and FHIR service are separate from authentication. Firely handles typed FHIR resource serialization and REST interactions. A bearer-token handler asks the configured token provider for a token on each outgoing request. Anonymous mode returns no token by explicit configuration; OAuth modes fail if token acquisition fails. Read retries are limited to safe HTTP methods.

The current local HAPI connector and SMART Sandbox connector have independent base URLs and authentication. Local requests use the existing local HAPI configuration and do not receive a sandbox bearer token. The sandbox OAuth handler uses ASP.NET Core OAuth middleware for protected state/correlation and PKCE S256; its bearer token remains in a process-local server-side session store. Sandbox FHIR requests are blocked unless their destination is beneath the exact configured sandbox issuer. Neither connection falls back to the other. SMART Backend Services remains a separate flow and was not part of this browser launch.
