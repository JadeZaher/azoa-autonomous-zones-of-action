# Tech Stack

## Runtime
- .NET 8 (LTS)
- ASP.NET Core WebAPI

## Language
- C# 12

## Key Libraries
- `Swashbuckle.AspNetCore` — Swagger / OpenAPI
- `Microsoft.AspNetCore.Authentication.JwtBearer` — JWT auth
- `System.IdentityModel.Tokens.Jwt` — Token handling

## Architecture
- Controllers → Managers → Providers
- Provider activation lives in a single `ProviderContext` abstraction
- `IOASISStorageProvider` is the unified provider interface
- `OASISResult<T>` / `OASISResponse` models for consistent API responses

## Patterns
- Unified Provider Pattern (replaces per-request manual activation)
- JWT Bearer authentication
- Async/await throughout
- Nullable reference types enabled
