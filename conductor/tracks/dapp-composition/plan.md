# Dapp Composition — Plan

## Tasks

1. [x] ~~Create `Models/Quest/DappSeries.cs`~~ — superseded: entity is source-gen'd into `OASIS.WebAPI.Generated.SurrealDb.DappSeries` from `Persistence/SurrealDb/Schemas/source/210_dapp_series.mermaid`. No hand-written model per the "POCOs from surreal package" directive.
2. [x] ~~Create `Models/Quest/DappSeriesQuest.cs`~~ — superseded: source-gen'd from `Persistence/SurrealDb/Schemas/source/220_dapp_series_quest.mermaid`.
3. [x] Create `DappManifest` — `Models/Requests/DappCompositionRequests.cs` (in-memory artifact, persisted as JSON string on `dapp_series.manifest`; not a separate aggregate).
4. [x] ~~EF Core configurations~~ — DEFERRED to surrealdb-migration wave-2 per the in-flight surrealdb cutover. New entities are POCO-only behind the `I*Store` seam; no EF Core involvement.
5. [x] ~~Add DbSets to `OASISDbContext`~~ — DEFERRED (same reason as task 4).
6. [x] Create `Interfaces/Managers/IDappCompositionManager.cs`.
7. [x] Create `Managers/DappCompositionManager.cs` with series CRUD, quest management, composition.
8. [x] Implement `ComposeAsync` with 5 validation rules (AllQuestsCompleted, ChainCompleteness, InputMappingConsistency, NoCircularDependencies, HolonBindingsResolved) + DappManifest emission.
9. [x] Implement `GenerateAsync` — calls `ISTARManager.CreateOrUpdateAsync` + `GenerateAsync` with `STARDappGenerationRequest`. Spec showed `TargetChain`/`BoundHolonIds` fields on `STARODKCreateModel` that don't actually exist on the runtime API; those flow via `STARDappGenerationRequest` instead.
10. [x] Implement `DeployAsync` — Ready-status guardrail + `ISTARManager.DeployAsync` delegation + status → Deployed.
11. [x] Register `IDappSeriesStore` (Singleton, InMemory) + `IDappCompositionManager` (Scoped) in `Program.cs` DI. Depends on `IDappSeriesStore`, `IQuestStore`, `IQuestRunStore`, `IHolonStore`, `ISTARManager`.
12. [x] Create `Controllers/DappSeriesController.cs` with series CRUD + quest management endpoints (`/api/dapp-series/...`).
13. [x] Create `Controllers/DappCompositionController.cs` with compose, validate, manifest, generate, deploy, status endpoints (`/api/dapp-series/{id}/...`).
14. [x] ~~EF Core migration~~ — DEFERRED to surrealdb-migration wave-2.
15. [x] Unit tests for composition validation rules — `tests/OASIS.WebAPI.Tests/DappCompositionManagerTests.cs` covers all 5 rules + status-machine guardrails + avatar scoping. 12 tests, all green.
16. [ ] Integration tests for full pipeline (series → compose → generate mock → deploy mock) — DEFERRED pending convention decision on POCO partial-class extension approach across all surreal tables.
17. [x] `dotnet build` — zero warnings.
18. [x] Tests — 535/535 passing.
19. [ ] Verify Swagger UI lists all dApp composition endpoints — pending manual smoke after deploy.

## Architecture decisions made during implementation

- **Source-gen'd POCOs are canonical for new entities.** `DappSeries` + `DappSeriesQuest` are emitted from `.mermaid` schemas; no hand-written models. This implements the user directive to "have POCOs being generated from the surreal package."
- **DappManifest stays as JSON-on-row.** Persisted in `dapp_series.manifest` as string; not promoted to a separate table because it's always read whole alongside its parent series.
- **STARODKCreateModel is the authoritative shape.** Spec text at `spec.md:179-186` shows fields (`TargetChain`, `BoundHolonIds`) on the create model that don't exist on the runtime `ISTARManager.CreateOrUpdateAsync` surface. Those fields flow via `STARDappGenerationRequest` to `GenerateAsync` instead. Spec is outdated; runtime API is the truth.
- **EF/DbContext removed from scope.** Per the in-flight surrealdb-migration wave-2 + the `greenfield-prelaunch-no-compat` project memory.
- **Avatar scoping enforced inside the manager.** `OwnedBy` check on every operation; returns "Forbidden" when avatar mismatch. Matches the spec's avatar-scoped access control requirement.
- **`Quest`/`QuestNode`/`QuestRun`/`QuestDependency` name collisions** between hand-written (`Models.Quest.*`) and source-gen'd (`Generated.SurrealDb.*`) handled with `using *Def = ...` aliases inside `DappCompositionManager.cs` and its tests. Eliminated once the broader Quest aggregate cuts over to source-gen POCOs (see partial-class extension convention discussion in `tracks.md`).
