# Startup Config — Specification

## Goal
Wire the application with a modern `Program.cs` (minimal-hosting style) or clean `Startup.cs`, register all services, configure JWT auth, Swagger, and the unified provider pattern.

## Requirements

### 1. Service Registration
- `ProviderContext` as scoped service
- `IAZOAStorageProvider` singleton or factory registration
- JWT Bearer authentication
- Swagger with bearer token support
- CORS policy (permissive for dev)

### 2. Middleware Pipeline
- Exception handling middleware
- HTTPS redirection
- CORS
- Authentication
- Authorization
- Swagger UI in Development
- Controllers

### 3. Configuration
- JWT secret from `appsettings.json`
- Provider configuration section (default provider, failover settings)

### 4. appsettings.json
- `Jwt:Key`, `Jwt:Issuer`, `Jwt:Audience`
- `AZOA:DefaultProvider`
- `AZOA:FailOverMode`

## Exclusions
- No Karma service registration
- No legacy `AvatarManager` singleton unless it fits the new pattern

## Acceptance Criteria
- [ ] `dotnet run` launches without errors
- [ ] Swagger UI shows Avatar and STAR controllers only
- [ ] JWT-protected endpoints reject anonymous requests
- [ ] ProviderContext resolves correctly per request
