# Tests

All .NET test projects for AZOA.WebAPI live here, alongside mutation-test output.

| Project | Type | Purpose |
|---------|------|---------|
| `AZOA.WebAPI.Tests` | xUnit | Unit tests for managers, controllers, providers, validation |
| `AZOA.WebAPI.IntegrationTests` | xUnit + `WebApplicationFactory` | In-process HTTP integration tests |
| `AZOA.WebAPI.LiveTests` | Console harness | JSONL-driven tests against a *running* API |
| `StrykerOutput/` | Artifacts | Stryker.NET mutation-test reports (git-ignored) |

## Running

From the repo root (or anywhere — the script resolves paths itself):

```powershell
# Unit + integration suites (the default "all tests")
./tests/run-tests.ps1

# Release configuration
./tests/run-tests.ps1 -Configuration Release

# Also exercise the live HTTP harness (needs a running API)
./tests/run-tests.ps1 -Live -LiveUrl https://localhost:5001

# Mutation testing -> tests/StrykerOutput
./tests/run-tests.ps1 -Mutation
```

Equivalent raw commands:

```bash
dotnet test azoa.sln                                   # unit + integration
dotnet run --project tests/AZOA.WebAPI.LiveTests             # live harness
dotnet stryker --output tests/StrykerOutput                   # mutation testing
```

> `AZOA.WebAPI.LiveTests` is a console `Exe`, not a `dotnet test` project, so it
> is excluded from `dotnet test` discovery and must be launched explicitly (see
> its own [README](AZOA.WebAPI.LiveTests/README.md)).

## Paths

These projects sit one directory deeper than before, so each `.csproj`
references the app with `..\..\AZOA.WebAPI.csproj`. The solution
(`azoa.sln`), `stryker-config.json`, and the main `AZOA.WebAPI.csproj`
source excludes all point at `tests/`.
