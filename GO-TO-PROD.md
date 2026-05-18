# GO TO PROD — OASIS API production-readiness

**Purpose:** the single gate between "code complete" and "real cross-chain value
flows." Greenfield / pre-launch: no customers, no data — bias to clean config,
fail-closed defaults, and verified trust roots, not compat shims.

**Companion docs (read together):**
- [conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md](conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) — what's safe, residual risks, stuck-bridge ops.
- [conductor/tracks/api-safety-hardening/GUARDIAN-SET-SETUP.md](conductor/tracks/api-safety-hardening/GUARDIAN-SET-SETUP.md) — Guardian-set trust-root procedure.
- [AGENTS.md](AGENTS.md) — build/test/stack ops.
- `scripts/passoff.ps1` — the automated **code** sign-off gate.

---

## 0. Status at last update (2026-05-18)

Code gate **GREEN**: prod build 0 errors; 564/564 unit tests; `scripts/passoff.ps1`
exit 0. Multi-agent review verdict **APPROVE-WITH-SIMPLIFICATIONS** (no
double-spend/replay/atomicity hole; localized overengineering to clean up — §4).
What remains is **ops/config sign-off**, not engineering, plus the §4 cleanup.

---

## 1. HARD launch gates (ALL must pass — no exceptions)

| # | Gate | How verified | Owner |
|---|------|--------------|-------|
| 1 | `scripts/passoff.ps1` exits 0 (build 0 err, full suite 0 fail, safety tests asserted green) | run it | Eng |
| 2 | Mainnet + testnet Guardian sets populated **and** byte-for-byte verified from the on-chain Core contract + a second authoritative source | GUARDIAN-SET-SETUP.md checklist signed | Ops |
| 3 | Live-network VAA validation on devnet/testnet against the real Guardian network (a real VAA verifies; a tampered one is rejected) | manual e2e | Ops+Eng |
| 4 | All secrets supplied from the secret store (NOT appsettings) — see §2 | deploy config audit | Ops |
| 5 | Target DB provisioned, empty, migrated cleanly (greenfield: start empty) | `db.Database.Migrate()` boot OR gated job; verify tables/indexes | Ops |
| 6 | `Reconciliation:Enabled=true`, `RateLimiting:Enabled=true`, `Blockchain:Wormhole:RequireFullSignatureVerification=true` | config audit | Ops |
| 7 | `Sagas:Enabled=false` (no consumer until durable-saga Phase 2) | config audit | Eng |
| 8 | Review pre-launch simplifications §4 items 1–4 landed | PR + tests | Eng |
| 9 | One SRE has read the RESIDUAL-RISK-RUNBOOK and signed off | sign-off line | Ops |

Wormhole value flow is **fail-closed** until gates 2+3 pass — this is by design,
not a bug.

---

## 2. Configuration requirements (every key; secrets flagged)

**SECRET — must come from the deploy secret store / env, never committed appsettings:**
- `ConnectionStrings:OASISDatabase` — prod Postgres DSN (host/port/db/user/**password**).
- `Jwt:Key` — signing key (long, random, rotated).
- `WalletEncryptionKey` — at-rest wallet key material. **Confirm overridden** (appsettings ships a placeholder; a real deploy MUST replace it before any wallet/seed is created).
- `Blockchain:Faucet:Algorand:Mnemonic` — faucet broadcaster seed (testnet only; do not fund on mainnet unless intended).

**REQUIRED non-secret config:**
- `Jwt:Issuer`, `Jwt:Audience`.
- `Blockchain:Wormhole:GuardianRpcUrl` — Guardian/Wormholescan RPC for the target network.
- `Blockchain:Wormhole:RequireFullSignatureVerification` = `true` (default; never `false` outside devnet/testnet).
- `Blockchain:Wormhole:GuardianSets` — `{ "<setIndex>": ["0x..20-byte..", ...] }`, list position = guardian index. **Per environment file.** devnet shipped+verified (`appsettings.Development.json`, index 0). **testnet + mainnet (index 4, 19 guardians) are NOT shipped — populate+verify per GUARDIAN-SET-SETUP.md. Absent ⇒ fail-closed (intended).**
- `Blockchain:*` chain/network endpoints per the network architecture (devnet/testnet/mainnet switch).
- `RateLimiting` — `Enabled`, `Global{PermitLimit,WindowSeconds,QueueLimit}`, `Financial{...}`. Defaults conservative; tune for expected load.
- `Reconciliation` — `Enabled=true`, `IntervalSeconds` (300), `Bridge*/Operation*` staleness + hard-stuck thresholds.
- `Sagas` — `Enabled=false` until durable-saga Phase 2 ships a consumer (see §4).

**Config validation:** consider `IValidateOptions<WormholeConfig>` + `ValidateOnStart()` so a misconfigured mainnet Guardian set fails at **boot**, not at first value flow (review §F.9).

---

## 3. Operations requirements

- **Database:** Postgres (EF migrations). Greenfield → start from an EMPTY DB; `Migrate()` on boot builds the full schema (incl. `AddIdempotencyAndBridgeSafetyConstraints`, `AddSagaOutbox`). Do NOT add "table already exists" compat shims — reset instead. SurrealDB replaces this later (`surrealdb-migration` track).
- **Backup/restore:** Postgres-era only; a restore drill is owned by `surrealdb-migration` G5. For the Postgres interim, ensure standard PITR/snapshot if any value flows before migration.
- **Container/runtime:** podman/docker Postgres for local/CI (`tests/run-tests.ps1` auto-spins it); prod uses managed Postgres. `.NET 8` host.
- **Rate limiting:** in-memory per instance. **Multi-instance scale-out needs a distributed store (Redis fixed-window)** before horizontal scaling — single instance is fine pre-launch.
- **Monitoring (must be wired before launch):**
  - Alert on log `MANUAL INTERVENTION REQUIRED` (ERROR) from reconciliation.
  - Scheduled query for hard-stuck bridges (RESIDUAL-RISK-RUNBOOK §3 SQL); none should exceed the hard-stuck threshold without escalation.
  - Watch `IdempotencyRecords` stuck `InProgress` and `SagaSteps` dead-letter (when saga enabled).
- **CI/CD:** `scripts/passoff.ps1` as a required pipeline stage; block deploy on non-zero exit.
- **Runbook accessible** to on-call (printed/linked).

---

## 4. Pre-launch cleanup from architecture review (APPROVE-WITH-SIMPLIFICATIONS)

The review explicitly defended the safety spine as *not* overengineered, but
flagged localized cleanup. **MUST land before launch** (small, high-value, align
with the no-overengineering intent):

1. **Remove the vestigial `xmin`/`Version` concurrency token** (`BridgeTransactionResult`, `SagaStepRecord`, `OASISDbContext` mappings, `SqliteTestDbContext` override). Never exercised — all flows use conditional `ExecuteUpdateAsync`; tests strip it. Pure deletion, zero behavior change. *(~1h)*
2. **`OperationStatus` enum/constants** for `BlockchainOperation.Status` (currently stringly-typed across producer + reconciler → silent-divergence risk). *(~1–2h)*
3. **`Sagas:Enabled=false` default** / pull saga DI from the prod composition until Phase 2 — a consumerless hosted loop + migration should not run in the pre-launch financial graph. *(~30m)*
4. **Add `BridgeStatus.Reversing`** (or a phase discriminator); replace the `IsReversalInFlight` `CompletedAt`-timestamp heuristic with explicit provenance. Highest-value correctness simplification. *(~½d incl. tests)*

**SHOULD (not launch-blocking):** extract `WormholeDigest.Canonical()` shared helper (kill the cross-class static reach-through); replace idempotency catch+reflection with `INSERT … ON CONFLICT DO NOTHING RETURNING` or an injected detector; migrate store scope-dance to `IDbContextFactory`.

**MUST gate durable-saga Phase 2 (not pre-launch, but a hard gate for that track):** `EfSagaStore` either implements real transactional-outbox semantics (explicit `IDbContextTransaction` around multi-write ops) **or** the spec/docs stop claiming "transactional outbox / same transaction." The no-broker justification currently rests on a property not yet implemented.

---

## 5. Known deferred (owned elsewhere — not launch blockers, tracked)

| Item | Owner track |
|------|-------------|
| Integration-test harness rebuild (vs SurrealDB, not Postgres patch) | `surrealdb-migration` |
| Durable-saga bridge adoption (Phase 2) + transactional-outbox fix | `durable-saga-orchestration` |
| Reconciliation tri-state provider (Confirmed/Dropped/Pending) | post-launch enhancement |
| Distributed rate-limit store (multi-instance) | post-launch / scale |
| API-key usage metering / billing | post-launch |
| SurrealDB single-engine migration (G1–G7) | `surrealdb-migration` |

---

## 6. Sign-off

| Gate | Signed by | Date |
|------|-----------|------|
| Code gate (`passoff.ps1` green) | | |
| §4 items 1–4 landed | | |
| Guardian sets verified (GUARDIAN-SET-SETUP.md) | | |
| Live-network VAA validation | | |
| Secrets audited (§2) | | |
| DB provisioned + migrated | | |
| Monitoring wired (§3) | | |
| Runbook read by on-call SRE | | |

**No real cross-chain value until every row above is signed.**

---

**Last updated:** 2026-05-18 (initial — post code-review APPROVE-WITH-SIMPLIFICATIONS)
