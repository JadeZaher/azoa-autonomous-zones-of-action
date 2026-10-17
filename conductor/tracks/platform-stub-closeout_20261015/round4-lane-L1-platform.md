---
type: lane-checkpoint
lane: L1-platform
wave: W2
branch: r4/platform
status: merged
merged: c56a8cbd
---

# Round 4 — L1 platform (W2 `r4/platform`, merge #3)

Base `feat/platform-stub-closeout` @ `beae947`. Closing vocabulary: **Built** (compiled; new and
already-failing unit tests run by filter) at most. The integration fixtures and the HTTP flow are
**Authored** (compiled, not run). The schema path change is **probed** on a throwaway SurrealDB
v3.1.4 (`--user 0:0`, port 8047, reaped). Rationale lives in the directory `AGENTS.md` files cited.

## Scope closed

| Item | Where |
|---|---|
| Operator/tenant retry from **any undelivered state with no live attempt**: `DeadLettered`, `Pending` at the attempt budget, `Pending` with an expired lease. A healthy backoff and a live lease stay refused | `Services/Webhooks/WebhookRetryEligibility.cs` (shared classifier); `Providers/Stores/Surreal/SurrealActionWebhookDeliveryStore.Management.cs:56,84-97` and `SurrealConsentWebhookDeliveryStore.Management.cs:56,83-99` |
| Version CAS also pins the observed status, lease id and cycle count; database-time guard `lease_expires_at <= time::now()` in the same batch as the registration lock and the retry audit; the lease is cleared so a woken holder cannot finish | same lines; rationale `Services/Webhooks/AGENTS.md` §operator-retry |
| Receipts record `RetriedFrom`; statuses expose `Retryable` (same classifier, so the UI never offers a retry the server refuses) | `Models/WebhookEventManagement.cs:5-19` |
| One attempt bound shared by workers, sender and retry | `Services/Webhooks/WebhookOptions.cs:41` (`BoundedMaxAttempts`); store options injected optionally (`Surreal*WebhookDeliveryStore.cs:18`) |
| `Webhooks:Enabled` on in Development, IntegrationTest, e2e; off in production until the sweep is green | `appsettings.Development.json`, new `appsettings.IntegrationTest.json`, `docker-compose.e2e.yml:111-112`; `Services/Webhooks/AGENTS.md` §activation |
| Outbox-drain HTTP flow (authored) | `tests/AZOA.WebAPI.IntegrationTests/Http/WebhookOutboxDrainFlowTests.cs` + `Http/Infrastructure/WebhookDrainWebApplicationFactory.cs` (new file in L6's infra dir); `Http/AGENTS.md` §webhook-drain |
| Governance re-blocked: `GovernanceProposalCreate`, `GovernanceVote`, `GovernanceTally` | `Services/Actions/NotEnabledActionNodeTypes.cs:9-15` |
| Verdict-gated lift documented and enforced: gate lifts only when `SOURCE_VERDICT` **and** `RUNTIME_VERDICT` are `ACCEPTED` | new `round4-governance-runtime-verdict.md` (`RUNTIME_VERDICT: PENDING`, lift procedure, fixture inventory); `tests/AZOA.WebAPI.Tests/Actions/NotEnabledActionNodeTypesTests.cs`; `Services/Governance/AGENTS.md` §governance-gate |
| `round2-governance-verification-migration.md` finished | appended §Round 4 closeout (each pending item mapped to a fixture or to the verdict procedure; one stated authoring gap) |
| Real-DB race fixtures (authored) | `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealGovernanceTests.Races.cs`: cancellation vs vote, cancellation vs creation, participation revocation vs vote, vote vs finalization across the cutoff, membership change vs creation snapshot |
| W1 handoff: deploy reconcile reverted migration-added constraints | `docker-entrypoint.sh:98-109` and `.github/workflows/ci.yml` schema job → `surrealforge up --no-reconcile`; new CI step "Deploy path converges to the generated model" (`ci.yml:283`); `Persistence/SurrealDb/AGENTS.md` §deploy-path |
| CI failure in owned area: `GovernanceCommandValidationTests.Invalid_creation_returns_typed_failure_without_writing` (8 cases) | stale mock (`QueryAsync` vs the store's keyed `QuerySingleAsync`); assertion tightened so the refusal must come from validation |
| `// lock:` on every in-batch read in changed stores | webhook retry batches; `SurrealGovernanceStore.cs` (electorate snapshot), `.Finalization.cs` (proposal row), `.Voting.cs` (KycEvidenceFence, claim row) |

## Reconcile decision (W1 handoff) — probe evidence

SurrealForge 1.3.0 `up` (`src/SurrealForge.Cli/Program.cs:160-330`) offers `--no-reconcile`,
`--assembly <dll>` and `--allow-destructive`; no flag reconciles against a different directory.
Same composed directory (Generated + CompatibilityBaselines, 131 files), fresh DB each:

| Variant | pass 1 | pass 2 | `INFO FOR TABLE` vs `Generated/Schemas` alone (131 tables) |
|---|---|---|---|
| default (today) | applied 144, evolved 1, destructive-skipped 75 | — | 1 difference: `operation_log.idempotency_key` loses its ASSERT |
| `--assembly bin/…/AZOA.WebAPI.dll` | applied 144, evolved 3, destructive-skipped 4 | **evolved 3 again** (not idempotent) | 0 |
| **`--no-reconcile` (chosen)** | applied 144, evolved 0, destructive-skipped 0 | applied 0, skipped 144, evolved 0 | **0** |

The new CI convergence step was executed locally against the probe: green on `--no-reconcile`,
red (exit 1, names `operation_log`) on the default path.

## Hunks

```hunk path=appsettings.Development.json
  "Webhooks": {
    "__comment": "Delivery workers run in Development, IntegrationTest and e2e; production stays off until the outbox-drain flow and delivery fixtures pass the sweep (Services/Webhooks/AGENTS.md §activation).",
    "Enabled": true
  },
```

```hunk path=appsettings.IntegrationTest.json
  "Webhooks": {
    "__comment": "IntegrationTest hosts run both delivery workers (Services/Webhooks/AGENTS.md §activation). Test receivers use reserved .example names (never resolve) or .flow.test names (answered by the VendorEdge).",
    "Enabled": true
  }
```

```hunk path=docker-compose.e2e.yml
      # Delivery workers run in e2e (Services/Webhooks/AGENTS.md §activation).
      Webhooks__Enabled: "true"
```

```hunk path=docker-entrypoint.sh
        --migrations-dir /app/persistence/SurrealDb/Migrations \
        --no-reconcile \
        --applied-by "azoa-local/docker-entrypoint"
```

```hunk path=.github/workflows/ci.yml
            --migrations-dir Persistence/SurrealDb/Migrations \
            --no-reconcile \
            --applied-by "ci/${{ github.sha }}" \
```

```hunk path=.github/workflows/ci.yml
      - name: Deploy path converges to the generated model
```

No `Program.cs` change.

## POCO deltas

None (no table changed; no schema regeneration needed). DTO deltas only: `WebhookEventStatus` /
`ConsentWebhookEventStatus` gain trailing `bool Retryable`; `WebhookEventRetryReceipt` gains
trailing `string? RetriedFrom = null` (older stored receipts deserialize to null).

## Contracts (plan §4.9 row "webhook operator retry (any undelivered state)")

Routes unchanged: `POST api/tenant/webhook-events/{eventId}/retry`,
`POST api/operator/tenants/{tenantId}/webhook-events/{eventId}/retry` and the two
`consent-webhook-events` twins. Body unchanged (`{ requestId, expectedVersion, reason }`).
Semantics widened as above; 409 when not retryable. Response adds `retriedFrom`
(`"DeadLettered" | "AttemptBudgetExhausted" | "AbandonedAttempt"`); list/get rows add
`retryable: boolean`. For L7, see Needs other lane.

## Deletions

- `IWebhookRegistrationStore.RotateSecretAsync` + implementation: unaudited secret rotation with no
  caller (the audited management command is the only rotation path).

## Compile result

`dotnet build azoa.sln -c Debug --no-incremental` → **0 errors, 15 warnings** (base 22; seven
pre-existing xUnit1031 cleared in `SurrealGovernanceTests.cs` and `SurrealWebhookRegistrationTests.cs`;
none added). `node scripts/round4/merge-check.mjs` → ok. `node scripts/round4/secret-scan.mjs .
"feat/platform-stub-closeout HEAD"` → no hits. No SDK/frontend file touched.

Unit tests run by filter (new or already failing in owned area): `WebhookRetryEligibilityTests`,
`SurrealDeploymentLeastPrivilegeContractTests`, `*WebhookDeliveryWorkerTests` (30 pass);
`NotEnabled*` (16 pass); `GovernanceCommandValidationTests` (10 pass, was 8 failing in CI).

## Checklists

**Transaction scope** (`round3-transaction-scope-review.md`), webhook retry batch: one
`BEGIN … COMMIT`; every pre-read fact used in the decision is pinned in-batch (event version,
status, lease id, cycle count, receipt, body; registration version/destination; tenant active;
operator auth time); the one plain read (`$_retry_before`) is of the row CAS-updated in the same
batch (`// lock:`); retry wrapper unchanged; loser re-reads the audit receipt; no chain call.
Governance stores unchanged except `// lock:` comments; each in-batch read names its serialization
row. Race fixtures assert the outcome of every serial order.

**Security** (changed routes: the four retry routes): authz unchanged (tenant `BindAsync` with no
acting tenant + `SigningAuthorityFence.ForConsent`, operator `RecentNodeOperatorSession` + 5-minute
auth time); acting-tenant refused; input unchanged and CAS-bound; replay/idempotency via the audit
id keyed by actor/tenant/event/request; no secret or payload in status (unchanged); widened states
cannot redirect a destination (registration pin unchanged). Residual: an abandoned attempt may
have been delivered, so a retry can duplicate; receivers dedupe on `X-Azoa-Idempotency-Id`
(documented). Governance: gate strictly narrower.

**§7 fail-closed classes**: binding names — the new raw statement binds nothing and its variable
comes from a uniquely scoped `AssignRecord`; array captures — single-record `AssignRecord`;
positional offsets — none read; strict validator — same mid-batch `LET` shape as `TryClaimAsync`;
lowercase-enum / explicit-NULL raw writes — none (typed CAS binds the enum name; captured null
lease id renders `= NONE`, an exact pin, not an owner/tenant guard); bare ids into `[References]`
— none; `;` in `Of` — none; ordinal casts / converter-less enums — none; statements after a
top-level RETURN — none.

**Flow-tests rules**: the drain flow uses a real transition, the real registration route, the
running worker and an independent receiver-side HMAC; asserts HTTP responses and delivered
requests, not call counts. The only arranged state is the crashed-worker lease. Store fixture
`Exhausted_pending_cycle_is_retryable_but_a_live_attempt_is_not` covers the budget origin (not
reachable over HTTP while the worker runs). Unit test table covers the classifier branches.
Declined: an HTTP case for `AttemptBudgetExhausted` (the live worker suspends it first; covered at
the store seam).

**Pruning gate**: no unused usings (IDE0005 clean); no new raw SurrealQL beyond the one database-time
guard (annotated `// raw:` on the batch); no catch-alls; `WebhookOptions.Enabled` doc trimmed to
one line with an AGENTS.md pointer; dead `RotateSecretAsync` removed.

## Findings

1. **SurrealForge (owner repo, record only):** the reconcile reader parses `on_ramp_lot.surql` as
   fieldless because of `recovery_audit_json`'s `"[]"` default (repro: that file alone, `up
   --dry-run` → 41 "remove" items; a plain literal default → 0). Harmless now that nothing
   reconciles or passes `--allow-destructive`.
2. **SurrealForge (owner repo, record only):** `--assembly` reconcile is not idempotent: it
   re-evolves `node_fee_settlement.{fee,gross,net}_amount` ASSERTs every run (comparer reads the
   `dec` literal / quote spelling as drift) and reports `executes`/`forked_from` `in`/`out` as
   removals.
3. **Deploy path (owner decision at merge time, 2026-10-17): `--no-reconcile` + generation
   reset.** Round 4's schema ships by resetting the database (`dev-up -ResetDb` locally; Railway
   SurrealDB volume reset before the next deploy), so W1's changed files need no baselines or
   forward migrations; a fresh database applied with `--no-reconcile` is exactly
   `Generated/Schemas` (probe above). After the reset, nothing evolves a shipped definition
   implicitly: a later field/index change on a deployed table ships as a forward
   `DEFINE … OVERWRITE` migration or another generation reset.
4. **Governance authoring gap (stated, not hidden):** no real-DB fixture races a local-graph
   mutation against finalization (both take `LocalEntityMutationFence.Holons()`); the runtime
   reviewer decides whether it blocks the lift.
5. `IWebhookRegistrationStore.UpsertAsync` is an unaudited writer with no production caller; only
   `SurrealWebhookRegistrationTests` (5 tests) use it. Pruning candidate once those invariants
   (encryption at rest, single identity under concurrent create, rotation keeps destination) are
   re-expressed over `MutateAsync`. Not done here to keep the lane bounded.

## Needs other lane

- **L7 (SDK `sdk/azoa-wallet/src/api/webhooks.ts:65`)**: add `retryable: boolean` to both event
  status types and `retriedFrom?: 'DeadLettered' | 'AttemptBudgetExhausted' | 'AbandonedAttempt'`
  to the retry receipt.
- **L7 (`frontend/src/components/webhooks/webhook-events.tsx:132`)**: show Retry when
  `row.retryable` instead of `row.status === 'DeadLettered'`; fixture
  `webhook-events.test.tsx:10` gains `retryable`.
- **L7 (`frontend/src/components/action-builder/node-catalog.ts:122-124`)**: the three governance
  entries say "not enabled" in prose but lack `status: 'preview'` (CredentialRevoke at `:90` has it);
  add it to match the backend gate.
- **L7/L6 (`frontend/e2e/journeys/webhook-registration.journey.ts:13`, `frontend/e2e/AGENTS.md:50`)**:
  comments still say delivery is disabled in e2e. Delivery is now on; the pinned receiver address is
  unroutable, so e2e events dead-letter. A listening e2e receiver (host-run fake beside the vendor
  fakes) would let a journey prove delivery.
- **L6**: `SurrealQlSourceRulesTests` `// lock:` rule can now include the webhook stores and
  `SurrealGovernanceStore*`; RouteAuthorization/CrossTenant gate tests should load
  `tests/AZOA.WebAPI.Tests/Architecture/Inventories/{RouteAuthorization,CrossTenant}/L1.json`.
- **L4 (`Services/Actions/AGENTS.md:474-477`)**: the "Governance cannot be trusted to count … tally
  reads raw child holons" bullet is stale; the reason is now "runtime verdict pending"
  (`Services/Governance/AGENTS.md` §governance-gate). The "exactly four" count at `:447` is correct
  again.
- **L4**: CI failures in L4 files seen while triaging, not touched: `HolonPublicationTests.CreateAsync_PublishesAGovernanceProposal_WhenAskedTo`
  (`Managers/HolonManager.cs`), `WalletAuthTokenParityTests` ×4 (`Managers/WalletAuthManager.cs`,
  "address, chainType and signature are required").
- **L2**: the drain flow provisions a child through `POST api/tenant/users`; if the D-6 KYB gate
  lands on tenant provisioning, seed `TestDataBuilders.KybApprovedTenant` in
  `WebhookOutboxDrainFlowTests.OnboardTenantAsync` callers.

## Merge-time additions (orchestrator signal)

- L3 ask: `Helpers/DevelopmentProviderSecrets.cs:21,43` also maps the `Fiat__Test__ConnectWebhookSecret`
  `.env` alias (Development only, same rules as the other aliases).
- L3 ask: `Controllers/Admin/ProjectTreasuryController.cs` ledger view shows the bare tenant id
  (`SurrealRecordGuid.BareId` of `user:<hex>`; empty stays empty).

## Decisions needed

None. Deploy path recorded in finding 3.
