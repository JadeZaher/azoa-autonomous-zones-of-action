# Workflow

## Development Methodology
TDD-light: write the API contract first, then the controller, then the manager / provider wiring. Tests are encouraged for manager/provider logic; controller-level tests via integration test host.

## Coverage Target
> 70% for manager and provider logic. Controllers covered by integration tests.

## Commit Convention
```
[<track>] <imperative verb> <subject>

<body optional>
```
Example: `[core-api] add unified ProviderContext activation`

## Quality Gates
1. `dotnet build` passes with zero warnings (nullable enabled)
2. `dotnet test` passes
3. Swagger UI launches and lists all expected endpoints
4. No Karma-related symbols in the final assembly
