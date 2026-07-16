---
type: workflow
---

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
1. `dotnet build AZOA.WebAPI.csproj -c Debug` passes with zero errors and no new warnings over the documented baseline
2. Unit, schema, and applicable SurrealDB integration tests pass
3. Swagger UI launches and lists all expected endpoints
4. No Karma-related symbols in the final assembly
5. Generated `.surql` and flowchart artifacts match their attributed POCO sources
6. Changed-file pruning passes: no avoidable raw SurrealQL, swallowed catch-all
   exceptions, duplicated local helpers, copied implementation docs, stale
   comments, or unused imports. Any required escape hatch is documented beside
   its invariant and in the active track.
