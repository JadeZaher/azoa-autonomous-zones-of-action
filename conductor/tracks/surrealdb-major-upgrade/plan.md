# SurrealDB Major Upgrade — Plan

Five phases. Linear dependency chain — each phase gates the next, no
parallelism. Estimated 3-5 days total once started; longest single risk
is the .NET SDK + integration-test revalidation in Phase C.

## Phase A — DECISION (½ day)

Land `DECISION.md` next to this plan, with:

- **Target major + patch** (e.g. `2.2.6` or `3.1.x`). Cite upstream
  release notes for the patch, not just the major.
- **Storage engine** (`rocksdb` vs `surrealkv`). If 2.x+ default-on
  surrealkv is chosen, restore `?sync=every`.
- **.NET SDK target version** confirmed compatible with the chosen
  server major.
- **Risk acceptance** — explicitly note whether 3.0.1 RecordId data-loss
  class is patched in the chosen version (link upstream issue or
  release-notes diff).

A1. Read upstream release notes for 2.x final patch + 3.x latest patch.
A2. Check `surrealdb.net` NuGet matrix — which client versions support
    which servers. Pin direction lands here.
A3. Write `DECISION.md` (~1 page). Reviewer signs off in plan PR.

**Exit criterion:** `DECISION.md` checked in. Every subsequent phase
quotes its choices.

## Phase B — Server image bump (½ day)

B1. Update `docker-compose.dev.yml` image tag.
B2. Update `podman-compose.yml` image tag.
B3. If `docker-compose.surrealdb.yml` exists, update it too. Search
    the repo for any other `surrealdb/surrealdb:v` references and
    update consistently.
B4. Update the storage URI to match DECISION.md. If switching back to
    `surrealkv://...?sync=every`, double-check the path form on the
    new major (1.x used `surrealkv:///data/db?sync=every`; 2.x+ may
    have tightened the URI parser).
B5. Confirm the `/surreal isready` healthcheck still works on the new
    image (it has been stable since 1.3 but worth a `podman exec` to
    verify the flag set hasn't moved).
B6. Run `./dev-up.ps1 -ResetDb` — server should come up healthy, but
    the API will likely fail on wire-format mismatch until Phase C.

**Exit criterion:** surrealdb container reaches `Up (healthy)` on the
new image. API failure is expected and triages to Phase C.

## Phase C — .NET SDK + Oasis.SurrealDb.Client (1-2 days, highest risk)

C1. Update the `surrealdb.net` package reference in
    [Directory.Build.props](../../../Directory.Build.props) (and any
    transitive constraint).
C2. Build `Oasis.SurrealDb.Client` — fix compile errors. Likely
    surfaces: rename of `SurrealResponse` shape, change in
    `JsonElement` vs typed result, change in connection-pool init.
C3. Re-run `Oasis.SurrealDb.Client.Tests` suite. Patch wire-format
    discrepancies (statement-result envelope, error detail extraction
    — `SurrealResponse.ExtractErrorText` has been hardened recently
    and may need a third extension if the new major changes the
    shape).
C4. Run integration tests against the new image. Focus areas in order
    of risk:
    a. `tests/Oasis.SurrealDb.Schema.Tests/Migration/MigrationRunnerLiveTests.cs` — schema apply.
    b. `tests/OASIS.WebAPI.IntegrationTests/Persistence/Surreal/Surreal*StoreTests.cs` — read/write roundtrip per store.
    c. `tests/OASIS.WebAPI.IntegrationTests/Gates/G1_CrashDurabilityTest.cs` — engine durability under kill -9.
    d. Everything else (Mcp tests are SkippableFact-guarded; will
       light up automatically if the underlying executor is sound).
C5. Audit the `SELECT * FROM type::record($_t, $_id)` workaround
    landed in [SurrealAvatarStore](../../../Providers/Stores/Surreal/SurrealAvatarStore.cs)
    and [SurrealHolonStore](../../../Providers/Stores/Surreal/SurrealHolonStore.cs).
    On 2.x+ the `WHERE id = $id` form may match again (the Thing-vs-
    string strictness has been relaxed in some patch releases). If it
    does, revert to the simpler `SurrealQuery.SelectById` for
    consistency with other stores. If not, keep the workaround.
C6. Audit any remaining `SurrealQuery.SelectById` / `DeleteById`
    callers (BlockchainOperationStore, BridgeStore use both forms in
    different places).

**Exit criterion:** every test suite green on the new major. No
`SkippableFact` was forcibly skipped due to executor failure.

## Phase D — G1 durability gate revalidation (½ day)

D1. Update Program.cs
    [line 553-558](../../../Program.cs) G1 acknowledgement error
    message to quote the URI from DECISION.md.
D2. Run `G1_CrashDurabilityTest` repeatedly (10× minimum) on the new
    engine. Confirm: writes acked before kill -9 are present after
    restart; writes mid-flight at kill time are NOT present (no
    half-applied state).
D3. If 3.x was chosen, add an explicit assertion that the RecordId
    issue does not affect our schema shapes (we use `ToString("N")`
    32-char hex IDs, not raw record-id literals — likely unaffected
    but verify).
D4. Update [data-engine-decision](../../../C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-oasis-sleek/memory/data-engine-decision.md)
    memory if any of the 7 guardrails shift.

**Exit criterion:** G1 test passes 10/10. Program.cs error message
matches reality. Memory updated if needed.

## Phase E — Docs + closeout (½ day)

E1. Sweep `README.md`, `DEVELOPMENT.md`, `RUNBOOK.md`, `PROVIDERS.md`,
    `API_SYNC.md`, `packages/Oasis.SurrealDb.Schema/DESIGN.md`,
    `packages/Oasis.SurrealDb.Schema/RUNBOOK.md`. Update every `1.5.4`
    reference. Verify no doc still claims `surrealkv` if we landed on
    `rocksdb`, or vice versa.
E2. Write `SIGN-OFF.md` with: chosen version, what changed, what was
    audited, what's left in known-followups (e.g. embedding behavior
    on the new HNSW implementation, if relevant).
E3. Move track entry from "Pending" to "Shipped" in [tracks.md](../../tracks.md).
E4. Wave-1 research notes under `.omc/research/surrealdb-migration-wave1/`
    document a 1.5.4-frozen state — DO NOT edit them. Add a
    cross-reference link in SIGN-OFF.md to clarify they're historical.

**Exit criterion:** `tracks.md` updated, SIGN-OFF.md complete,
`./dev-up.ps1` and full test suite green on the new major.

## Risks + mitigations

| Risk | Mitigation |
| --- | --- |
| .NET SDK breaking change blocks Phase C indefinitely. | Phase A pins SDK version explicitly; if no compatible SDK exists for the chosen server major, DECISION.md must pick a different server major. |
| 3.x RecordId data-loss bug present in chosen patch. | Phase A blocks if upstream issue is open; fall back to 2.x. |
| G1 durability semantics changed silently between majors. | Phase D's 10× crash test makes regression detection mandatory before sign-off. |
| Docs drift not caught until a contributor follows a stale RUNBOOK step. | Phase E sweep is gated by SIGN-OFF; reviewer ack confirms doc parity. |

## Out of scope (explicit, repeated from spec)
- Data backfill / migration (no production data; greenfield repo).
- `oasis-surreal` CLI surface changes (engine bump, not tooling redesign).
- HNSW / embedding behavior re-engineering (audit yes, redesign no).
