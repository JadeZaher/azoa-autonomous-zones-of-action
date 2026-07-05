# Architecture Decoupling & Observability — Specification

## Goal
Tier 1: reduce structural debt and introduce the seams required before any
storage-engine change. No engine work here — this makes the SurrealDB migration
a contained change rather than a rewrite.

## Targets (file:line evidence)

### Thin persistence seam (the key enabler)
Today there are **two overlapping storage abstractions**: the god interface
`IAZOAStorageProvider` (41 methods, 11 entities — `IAZOAStorageProvider.cs` +
`IAZOAStorageProviderNFTExtensions.cs`) and a redundant `IQuestRepository`
(`Interfaces/IQuestRepository.cs` + `Services/Quest/QuestRepository.cs`), both
wrapping `AZOADbContext`. Collapse both into **one coherent set of
per-aggregate, graph-aware interfaces** (Avatar/Wallet/Holon/Quest/NFT/Bridge).
This single seam is what isolates the later SurrealDB SDK behind one file.
**Not** a generic `IRepository<T>` — that is an anti-pattern over a graph DB.

### God manager
`QuestManager` (9 ctor deps, 908-line file) has a ~315-line, 34-case
`ExecuteNodeInternalAsync` switch (`QuestManager.cs:317-631`). Extract an
`IQuestNodeHandler` strategy map (one handler per `QuestNodeType`, 34 values in
`QuestEnums.cs:19-70`). No such type exists today.

### Duplicated computation
`ExecutionOrder` is computed twice — `QuestDagValidator.cs:97-102` **and**
`QuestManager.cs:187-196` — a latent divergence bug. Single authoritative
computation.

### Static cache
`SwapManager.cs:29-30` — `static readonly` dictionary + lock, no size cap,
cleanup only on insert. Replace with `IMemoryCache` (not used anywhere yet).

### Observability (prerequisite for the migration)
No OpenTelemetry / tracing / correlation / metrics; no `/health` endpoint
despite a rich `IProviderHealthMonitor`. This must land **before** the
SurrealDB cutover so the unfamiliar engine is observable.

### Cleanup
- `ProviderContext.Activate()` returns the provider instead of storing mutable
  scoped state (`Core/ProviderContext.cs`, registered Scoped `Program.cs:109`).
- Implement or delete declared-but-unimplemented `AutoFailOver`/`AutoReplication`.
- Move startup `db.Database.Migrate()` (`Program.cs:243`) to a gated job
  (moot once EF is removed, but correct in the interim).

## Acceptance
- One per-aggregate persistence interface set; god interface +
  `IQuestRepository` deleted; all managers depend only on the new seam.
- `QuestManager` node dispatch is a handler registry; the 315-line switch is
  gone; open/closed for new node types.
- `ExecutionOrder` computed once.
- Swap quote cache is `IMemoryCache` with bounds + expiry.
- OpenTelemetry traces + `/health` live; request correlation in logs.

## Dependencies
Should follow `api-safety-hardening` (don't refactor unsafe value paths).
Blocks `surrealdb-migration` (the seam is its precondition).
