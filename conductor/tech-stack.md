---
type: tech-stack
---

# Tech Stack

## Runtime
- .NET 10
- ASP.NET Core WebAPI

## Language
- C# 14

## Key Libraries
- `Swashbuckle.AspNetCore` — Swagger / OpenAPI
- `Microsoft.AspNetCore.Authentication.JwtBearer` — JWT auth
- `System.IdentityModel.Tokens.Jwt` — Token handling
- `SurrealForge.Client` / `.Schema` / `.Analyzer` — parameterized SurrealDB access, schema emission, and query safety
- Next.js frontend and `@azoa/wallet-sdk` TypeScript client

## Architecture
- Controllers → Managers → Services / Stores / Providers
- SurrealDB is the sole datastore; attributed POCOs emit SCHEMAFULL `.surql`
- Blockchain providers implement chain-specific value/signing behavior behind manager interfaces
- `AZOAResult<T>` / `AZOAResponse` models for consistent API responses

## Patterns
- Role-first folders with namespaces mirroring directories
- JWT Bearer authentication
- Async/await throughout
- Nullable reference types enabled
