# Tests — Specification

## Goal
Validate the lightweight API with integration tests covering the full request/response cycle and unit tests for `ProviderContext` logic.

## Test Projects
- `OASIS.WebAPI.Tests` — xUnit
- `OASIS.WebAPI.IntegrationTests` — xUnit + `WebApplicationFactory`

## Coverage Targets
- Manager / Provider logic: > 70%
- Controllers: integration tests for happy path + auth failures

## Integration Tests
- `AvatarController` — register, login, CRUD with auth
- `STARODKController` — CRUD with auth
- Provider switching via header/body

## Unit Tests
- `ProviderContext` activation logic
- `OASISResult<T>` serialization edge cases

## Exclusions
- No Karma tests

## Acceptance Criteria
- [x] `dotnet test` passes with 100% of written tests green (256 tests)
- [x] Integration tests use an in-memory provider or mock (EF InMemory + WebApplicationFactory)
- [x] Auth tests verify 401 on missing token
- [x] Stryker mutation score ≥ 50 % break threshold (achieved 59.41 %)
