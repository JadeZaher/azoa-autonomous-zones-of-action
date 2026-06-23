# SurrealDB Major Upgrade — DECISION

**Status:** Decided 2026-06-12. Supersedes the spec's default lean toward 2.x.

## Target

| Choice | Value | Rationale |
| --- | --- | --- |
| **Server major + patch** | **`surrealdb/surrealdb:v3.1.4`** | Newest stable as of 2026-06-10. The 3.1 line (released 2026-05-27) is the explicitly *stability-focused* minor — "a thorough security pass, many correctness fixes... continues the stream of fixes that shipped in v3.0.1 through v3.0.5." v3.1.4 is the 4th patch on that line, two days old at decision time. Image pulled and confirmed on the dev machine. |
| **Storage engine** | **`rocksdb`** (keep current) | Already what compose ships; durable, no migration churn. SurrealKV is available default-on in 3.x but switching engines is orthogonal risk we are not taking in this cutover. Revisit `surrealkv://...?sync=every` as a separate follow-up if the G1 durability review wants it. |
| **.NET client** | **Homebake `Azoa.SurrealDb.Client` v0.1.0 (unchanged)** | This repo does NOT use the upstream `surrealdb.net` NuGet — it speaks raw HTTP `/rpc` via `HttpSurrealConnection`. There is therefore no third-party SDK version matrix to satisfy; the client work is auditing/fixing the SurrealQL wire surface our own client emits against the 3.1 server. |

## Risk acceptance — 3.0.1 RecordId data-loss class

The spec blocked on whether the 3.0.1+ RecordId data-loss / record-reference class is patched.

- Upstream issue [#6267 "Stabilize Record References in 3.x"](https://github.com/surrealdb/surrealdb/issues/6267) is **Closed**.
- 3.1 is the stabilization minor that absorbed the v3.0.1–v3.0.5 fix stream.
- **Our exposure is structurally low regardless:** our record ids are 32-char `Guid.ToString("N")` hex strings, not raw record-reference link types — the data-loss class concerns `record<...>` reference fields, which our schemas do not yet use for FK links (FK rewrite is the separate `data-backfill-migrations` / F6 track). Phase D adds an explicit assertion.

**Decision:** accept v3.1.4. The user explicitly chose "latest stable 3.x." The blocking condition (open data-loss issue on the chosen patch) is not met.

## What this unblocks / changes vs. the plan

- **Phase A2/C1–C3 (surrealdb.net SDK matrix) largely evaporate** — homebake client, no external SDK pin. Phase C becomes a pure SurrealQL-surface audit against the live 3.1.4 server.
- The live `MigrationRunnerLiveTests` `UPSERT ... CONTENT` parse error observed on 1.5.4 is expected to be a wire-surface delta to validate against 3.1.4, not a code bug — Phase C confirms.
- `DEFINE ... IF NOT EXISTS` is honored correctly on 3.x (the 1.5.4 "always emits already-exists ERR" quirk is gone), so the MigrationRunner idempotency complaint resolves at the engine layer — no tolerance shim needed.

## Sources
- [SurrealDB 3.1 blog](https://surrealdb.com/blog/surrealdb-3-1-stability-diskann-and-a-new-release-process) (2026-05-27)
- [GitHub releases](https://github.com/surrealdb/surrealdb/releases) — v3.1.4, 2026-06-10
- [Issue #6267 — Stabilize Record References (Closed)](https://github.com/surrealdb/surrealdb/issues/6267)
