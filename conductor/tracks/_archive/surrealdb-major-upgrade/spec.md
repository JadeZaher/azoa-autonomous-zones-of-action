# SurrealDB Major Upgrade — Specification

## Status
Pending. Created 2026-06-12. **Tier 1** (infrastructure / data plane).
Greenfield — no production users, no migration of customer data.
Decision exists because the codebase is pinned to **1.5.4** across compose
files, schema package, .NET SDK wrapper, and three docs surfaces, and
the upstream project has shipped both **2.x** (late 2024) and **3.x**
(2026). Staying on 1.5.4 is a deliberate, expensive choice — the
question is whether to move to 2.x or 3.x and pay the migration cost
now while the surface area is smallest.

## Goal
Pick a target SurrealDB major (2.x or 3.x), migrate the codebase to it
end-to-end (server image, .NET SDK, SurrealQL surface, compose
healthchecks, durability gate, integration test harness), and ship a
"green CI on day one" cutover with no surprise regressions in dev or
test environments.

## Why (decision rationale)
This track exists because three workstreams collided on the same day
(2026-06-12) while fighting a dev-up regression:

1. **The 1.5.4 image lacks the `surrealkv` feature flag.** We discovered
   it on launch — `surrealdb/surrealdb:v1.5.4` is built without
   `storage-surrealkv`, so the compose command `surrealkv:///data/db?
   sync=every` crashes with *"Cannot connect to the surrealkv storage
   engine as it is not enabled in this build"*. Worked around by
   switching to `rocksdb:///data/db` in [docker-compose.dev.yml:46]
   (../../../docker-compose.dev.yml). `surrealkv` ships
   enabled-by-default starting in **2.x** — if we ever want it back,
   we must upgrade.

2. **The .NET SDK is two majors behind the server.** `surrealdb.net`
   (which `Azoa.SurrealDb.Client` wraps) historically trails server
   majors. Today we are pinned to a `1.5.x`-compatible client because
   the server is 1.5.4. Bumping the server without coordinating the
   SDK breaks the wire layer.

3. **3.0.1+ has documented data-loss + perf-regression bugs.**
   [persona-archaeological.md](../../../.omc/research/surrealdb-migration-wave1/persona-archaeological.md)
   logs a RecordId data-loss class on 3.0.1+ and "documented
   performance regressions that were not caught before release."
   2.x avoids both. 3.x may need a specific patch version (3.0.x vs
   3.1.x) to be safe.

The choice between 2.x and 3.x is not obvious — 3.x has the longer
upstream future, 2.x has the cleaner risk profile. This track
forces a real decision instead of letting the 1.5.4 pin rot in place
for another year.

## Scope

### In scope

1. **Decision deliverable.** A `DECISION.md` in this track folder that
   lands a target major + patch (e.g. "2.2.6") with a one-paragraph
   rationale citing the .NET SDK matrix, the storage engine surface
   we depend on (RocksDB vs SurrealKV vs TiKV), and the upstream
   release-stability evidence.

2. **Server image bump.** Update `docker-compose.dev.yml`,
   `podman-compose.yml`, and any standalone surrealdb compose variants
   (`docker-compose.surrealdb.yml` if it exists) to the chosen image
   tag. Revisit the storage URI choice — `rocksdb` may still be right
   even on 2.x/3.x, or we may want to flip back to `surrealkv` if the
   image-version supports it AND `?sync=every` semantics are stable on
   that major.

3. **.NET SDK bump.** Update `surrealdb.net` package reference and any
   transitive constraint in [Directory.Build.props](../../../Directory.Build.props).
   Re-validate `Azoa.SurrealDb.Client` against the new SDK — wire
   format, statement-result shape, error envelope, and connection
   pool semantics may have shifted.

4. **SurrealQL audit.** The schema package has 1.5.x-specific comments
   embedded — e.g. `CONTENT does not support WHERE bind in
   SurrealDB 1.5.x`, `WHERE id = $id` Thing-vs-string behavior — plus
   the `type::record($_t, $_id)` workaround we just landed for the
   avatar/holon stores. Walk every store under
   [Providers/Stores/Surreal/](../../../Providers/Stores/Surreal/) and
   confirm each query is valid + idiomatic on the chosen major.

5. **Healthcheck audit.** The compose healthcheck now uses
   `/surreal isready --conn http://localhost:8000` because curl isn't
   in the distroless image. Confirm the `isready` subcommand still
   exists with the same flag set on the new major (it has been stable
   since 1.3, but worth re-checking).

6. **G1 durability gate revalidation.** Program.cs
   [line 553-558](../../../Program.cs) ties G1 acknowledgement to a
   specific storage URI. After the bump, re-confirm the URI string in
   the error message matches what compose ships AND that the chosen
   engine actually fsyncs per commit on the new major.

7. **Integration test harness.** Re-run every
   `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/*` and
   `tests/Azoa.SurrealDb.Schema.Tests/*` suite against the new major.
   The G1 crash-durability test is the highest-risk surface — engine
   behavior under `kill -9` is the exact thing 3.0.1's
   RecordId-data-loss bug would expose.

8. **Docs sync.** `README.md`, `DEVELOPMENT.md`, `RUNBOOK.md`,
   `PROVIDERS.md`, `API_SYNC.md`, and the schema package's `DESIGN.md`
   + `RUNBOOK.md` all reference 1.5.4. One pass to update all version
   strings + supporting context.

### Out of scope

- **Data backfill / migration.** Greenfield repo — no production rows
  to rewrite. The first real `data-backfill-migrations` consumer is
  F6 FK rewrite (separate track), which is post-server-upgrade by
  default.
- **`azoa-surreal` CLI surface changes.** The schema-apply CLI
  contract (`up`, `reset`, `migrate`) stays identical — this is an
  engine-version bump, not a tooling redesign.
- **Embedding / HNSW behavior change.** If the new major changes
  vector-index semantics, file a separate follow-up track. This
  track ships the upgrade with whatever the chosen major's HNSW
  semantics are, audited but not re-engineered.

## Why (decision rationale, condensed for catalog)
1.5.4 is pinned everywhere and the upstream has shipped two majors.
The dev-up regression on 2026-06-12 exposed the `surrealkv` feature-
flag mismatch and forced a temporary RocksDB workaround; this track
makes the version decision deliberate instead of letting the pin rot.
2.x is the safer pick (surrealkv default-on, no known data-loss class);
3.x is the longer-future pick. Land DECISION.md first.

## Open questions
- **2.x vs 3.x.** Default lean is 2.x for safety; if upstream has
  patched 3.0.1's data-loss class by the time this track starts, 3.x
  may win. Track DECISION.md is where this lands.
- **`surrealkv` vs `rocksdb`.** On 2.x+, surrealkv is enabled by
  default and has the `?sync=every` durability option we originally
  wanted. RocksDB is what we ship today. Either works; decision
  should be in DECISION.md.
- **Test-namespace isolation.** Per
  [open-decisions-2026-06-12.md](../../../C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/open-decisions-2026-06-12.md)
  there is still an open per-test namespace isolation gap. Confirm
  whether the new major changes the namespace-create cost enough to
  unblock the STARODK IDOR tests.

## Acceptance criteria
- [ ] `DECISION.md` lands target major + patch with rationale.
- [ ] `docker-compose.dev.yml` + `podman-compose.yml` images bumped.
- [ ] `surrealdb.net` SDK bumped + `Azoa.SurrealDb.Client` re-validated.
- [ ] All integration tests green against new major.
- [ ] G1 crash-durability test still passes (or replaced if engine
      semantics changed).
- [ ] Docs sweep: `README.md`, `DEVELOPMENT.md`, `RUNBOOK.md`, schema
      package `DESIGN.md` + `RUNBOOK.md` updated.
- [ ] `./dev-up.ps1` (default flags) brings the new stack up green.

## Related
- Memory: [data-engine-decision](../../../C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/data-engine-decision.md) — SurrealDB sole engine; durability guardrails.
- Memory: [surrealdb-fsync-mode-not-introspectable](../../../C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/surrealdb-fsync-mode-not-introspectable.md) — why G1 is a deploy-time review, not a runtime probe.
- Research: [.omc/research/surrealdb-migration-wave1/](../../../.omc/research/surrealdb-migration-wave1/) — five-persona deep-dive on the SDK + image surface at 1.5.4 freeze.
- Adjacent track: [data-backfill-migrations](../data-backfill-migrations/spec.md) — F6 FK rewrite consumes the post-upgrade schema shape.
