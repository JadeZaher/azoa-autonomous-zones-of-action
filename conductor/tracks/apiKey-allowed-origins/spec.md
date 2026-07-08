---
type: spec
track: apiKey-allowed-origins
created: 2026-07-08
status: shipped
horizon: launch-blocker
depends_on: []
---

# apiKey-allowed-origins — dynamic allowed origins per API Key & secure dashboard CORS

## Why

To make the AZOA SDK a highly public, frictionless open standard (similar to HTML or SQLite for Web3), third-party developers must be able to consume the SDK from any origin by default. 

However, we also need to allow:
1. **Dynamic CORS Security per key:** Developers or avatars may optionally lock their API keys down to specific trusted domains (e.g. `AllowedOrigins = "https://my-dapp.com"`).
2. **Secure Node Administration:** Administrative/JWT-based requests (like logging in to the node's official dashboard) must be restricted to the node operator's configured official domains (`Cors:AllowedOrigins` allowlist) to prevent Cross-Site Request Forgery (CSRF).

Currently, the production CORS policy strictly blocks all origins unless they are statically declared in the backend's allowlist. We will replace this with a custom dynamic CORS policy provider.

---

## Model (decided)

1. **Third-Party SDK / API Key Requests (contains `X-Api-Key`):**
   - Check the specific API key's `AllowedOrigins` field in the database.
   - If `AllowedOrigins` is empty or null (default): **Allow any origin (`*`)**.
   - If non-empty: Only allow origins matching the list.
2. **Dashboard / JWT / Login Requests (no `X-Api-Key`):**
   - Restricted strictly to the node operator's configured `Cors:AllowedOrigins`.
   - Outside of Dev/Test environments, if `Cors:AllowedOrigins` is empty, the application will fail-fast at startup.

---

## Backend Implementation Plan

### 1. Data Model & DB Schema
- **`Models/ApiKey.cs`**: Add the missing `Scopes` property (`public string? Scopes { get; set; }`) back.
- **`Persistence/SurrealDb/Models/ApiKey.cs`**: Add the `AllowedOrigins` property to the SurrealDB POCO.
- **`Providers/Stores/Surreal/SurrealApiKeyStore.cs`**: Map `AllowedOrigins` in `FromDomain` and `ToDomain` and update the private `ApiKeyPoco`.
- **`Persistence/SurrealDb/Generated/Schemas/api_key.surql`**: Add the `allowed_origins` field definition.

### 2. DTOs & Controller
- **`Controllers/ApiKeyController.cs`**:
  - Add `AllowedOrigins` to `CreateApiKeyRequest`, `CreateApiKeyResponse`, and `ApiKeyInfo`.
  - Map `AllowedOrigins` from request to model during creation/rotation.

### 3. Authentication & Scope Claim Fix
- **`Services/Auth/ApiKeyAuthenticationHandler.cs`**: Fix the `new Claim` compiler namespace ambiguity by explicitly using `new System.Security.Claims.Claim`.

### 4. Middleware: Dynamic CORS Policy Provider
- **`Middleware/DynamicCorsPolicyProvider.cs`**: Create a custom implementation of `ICorsPolicyProvider` that dynamically checks the HTTP context:
  - For preflight `OPTIONS` requests: dynamically echoes the request's `Origin` to allow the preflight to pass (since headers like `X-Api-Key` are not yet available).
  - For actual requests:
    - If `X-Api-Key` is present: reads the key, matches its `AllowedOrigins`.
    - If `X-Api-Key` is absent: falls back to the node operator's configured `Cors:AllowedOrigins` (in Production) or permits any origin (in Dev/Test).

### 5. Startup Wiring
- **`Program.cs`**: Register the dynamic policy provider `builder.Services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>()`.
