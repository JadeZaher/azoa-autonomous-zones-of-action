---
type: lane-checkpoint
lane: L1-schema
wave: W1
branch: r4/schema
status: ready-to-merge
---

# Round 4 — L1 schema (W1 `r4/schema`)

Base `feat/platform-stub-closeout` @ `f968955`. Rationale for every decision below lives in
`Persistence/SurrealDb/AGENTS.md` (new); this record is the evidence index. Closing vocabulary:
**Built** + schema-gated (applied twice to a throwaway SurrealDB v3.1.4); no test suite ran except
the generator (golden tests in write mode).

## Scope closed

| Item | Where |
|---|---|
| Regenerated every schema golden + flowcharts from the POCOs (generator: `AttributePocoByteEquivalenceTests` + `FlowchartRegenerationTests` with `AZOA_REGENERATE_GOLDENS=1`) | `Persistence/SurrealDb/Generated/**`: **61 new tables, 23 changed**, 131 files = 131 `[SurrealTable]` POCOs; 5 new flowchart slices (`_unassigned`, `deployment`, `ethereum_custody`, `swap`, `wtt`) |
| Second regeneration on the committed tree is a no-op (`git status` clean) | gate |
| CI schema-job failure (`type::is::number`, HTTP 400 on 3.1.4; also the likely E2E API exit 3) | `Migrations/20260821_120000…:22` → `type::is_number`. Edited in place: added after Railway's `c6c55dd`, unparseable on every 3.x DB, so no ledger can hold its checksum |
| CI perf-budget failures (`Generated schema golden not found for table 'local_entity_mutation_lock'`) | golden now exists (regeneration) |
| `[Column(Name = "secret")]` | `Models/WebhookRegistration.cs:17` (generator names columns from the property, not `[JsonPropertyName]`, so `LegacySecret` had emitted `legacy_secret`) |
| Defensive stale nonce-index removal | `Migrations/20261017_120000__remove_stale_ethereum_preparation_nonce_index.surql` — probed: no-op on absent index and absent table |
| Unique-index audit | `Persistence/SurrealDb/AGENTS.md` §unique-index-audit (table below) |
| Old-row rule | §old-row-rule; 32 POCO attributes → `DEFAULT ALWAYS`; backfill `Migrations/20261017_122000__backfill_old_row_counters.surql` |
| `[Inside]` ↔ domain enums | §inside-audit — no drift |
| Baseline drift found on the deploy path | `Migrations/20261017_121000__converge_kyc_submission_identomat_and_aml_lineage.surql`; `Models/KycSubmission.cs:21` index |
| POCO↔table map | all 131 tables match a plan §4.8 glob; none unplaced |

## Schema gate (deploy path, SurrealDB v3.1.4 `--user 0:0`, port 8040, reaped)

Composed exactly like `ci.yml` / `docker-entrypoint.sh` (Generated + CompatibilityBaselines, 131 files):

```
pass 1: applied 144, skipped 0, 0 mismatch(es), evolved 1, destructive-skipped 75
pass 2: applied 0, skipped 144, 0 mismatch(es), evolved 0, destructive-skipped 75
```

`INFO FOR TABLE` diff, deploy path vs `Generated/Schemas` applied alone: **1 difference** —
`operation_log.idempotency_key` loses its ASSERT (phase-3 reconcile, see §For W2 lanes L1). Before
W1's migrations there were 4 (also `$kyc_submission_provider` without `IDENTOMAT`,
`kyc_submission.aml_lineage_key` missing, index mismatch).

Upgrade simulation: apply the `f968955` tree, then this tree → `checksum mismatch detected for 22
file(s)` (exit 2). See Decisions needed.

## Tables / fields added

- New tables (61): action_emit_receipt, action_local_receipt, action_recovery_audit,
  action_run_source, action_signing_authorization, consent_lifecycle_command,
  consent_lifecycle_transition, consent_participation_approval, consent_participation_boundary,
  deployment_artifact, deployment_operation, deployment_recovery_audit,
  deployment_recovery_audit_entry, dex_quote, ethereum_dex_allowance, ethereum_preparation,
  ethereum_signer_checkpoint, exchange_hold_settlement_receipt, exchange_hold_source,
  exchange_purchase_allocation, fungible_asset_authority, fungible_creation_{attachment,
  authorization, cancellation, operation, projection}, governance_command_receipt,
  governance_proposal, hold_command, hold_command_authorization, hold_command_receipt,
  hold_mandate_use_receipt, hold_recovery_receipt, hold_release_mandate, hold_release_mandate_offer,
  hosted_provider_attempt, hosted_provider_callback, hosted_session_provider_alias,
  kyb_verification, local_entity_mutation_lock, native_preparation, native_quote,
  nft_asset_authority, nft_lifecycle, nft_protocol_cost_quote, nft_workflow_cost_acceptance,
  nft_workflow_cost_retirement, node_governance_policy_lock, swap_operation, swap_stage,
  wallet_auth_challenge_budget, wallet_auth_identity_migration, webhook_event_retry_audit,
  webhook_registration_audit, wtt_body_claim, wtt_evidence_progress, wtt_offer, wtt_operation,
  wtt_operation_stage, wtt_preparation, wtt_stage_key.
- 117 fields added to 23 pre-round-1 tables (85 `option<>`, 32 `DEFAULT ALWAYS`), 1 changed
  (`webhook_registration.secret` → optional legacy column), 5 index changes. Full list:
  §old-row-rule.

## Unique-index audit (summary; full table in `Persistence/SurrealDb/AGENTS.md`)

| Invariant | Enforcement |
|---|---|
| nonce `(protocol, sender, nonce, nonce_slot)` | UNIQUE `ethereum_preparation_live_nonce` (all non-optional) + record id |
| KYC person per tenant (D23) | UNIQUE `kyc_identity_scope_person` + record id |
| hosted admission | UNIQUE `hosted_session_admission` (non-optional; session ids random) |
| provider attempt / callback | record id = session id / digest(adapter, origin, event) |
| idempotency key | UNIQUE `idempotency_key_unique` + record id |
| fiat event id | UNIQUE `fiat_event_provider_event` + record id |
| on-ramp lot per intent | UNIQUE `on_ramp_lot_intent` + record id |
| payout per request | UNIQUE `fiat_payout_request` + record id |
| clawback receipt | record id `hash(lot, source)`; idempotency keys |
| WTT body / VAA | UNIQUE `wtt_publication_identity`, `consumed_vaa_*` + record ids |
| swap operation | record id per (actor, tenant, key); one swap per quote = transactional CAS on `consumed_by_operation_id = NONE` |
| DEX quote | random immutable id; consumption CAS |
| NFT lifecycle | record id `ForRequest`; UNIQUE `nft_authority_*` |
| fungible creation | record id; UNIQUE `fungible_asset_identity`, `*_operation` |
| hold command | record ids; UNIQUE `hold_ledger_hold` (partial on NONE tenant/protocol/owner — record id covers), `hold_mandate_use_operation` |
| consent grant | record ids from (grantor, request) in one transaction |
| wallet address | UNIQUE `wallet_user_chain_address` + deterministic id; `user_auth_wallet` |

**Gaps fixed: none needed** — every create-if-absent / unique-violation path
(`IsUniqueViolation` in 60+ store sites) has a record-id collision or a UNIQUE index behind it.
Probed semantic recorded for every reviewer: **NONE components escape a UNIQUE index** (single or
composite); the eight partial indexes are by design (AGENTS.md list).

## Old-row treatments that needed more than `option<>`

`[Default("ALWAYS …")]` (plain `DEFAULT` was probed to leave old rows unwritable on 3.1.4:
`Expected int but found NONE`), plus one backfill migration so CAS predicates see the value:

| POCO:line | Fields |
|---|---|
| `ActionNodeExecution.cs:29,37` | version, manual_advance_released (had no default at all) |
| `ActionRun.cs:30` | version |
| `ConsentGrant.cs:29` | version |
| `ActionWebhookEvent.cs:29-31`, `ConsentWebhookEvent.cs:34-36` | version, retry_generation, cycle_attempt_count |
| `IdempotencyKeyStore.cs:61` | preparation_generation |
| `FiatPayout.cs:184-231` | bank_payout_attempt, environment, chain_network, recovery_requested, recovery_reversal_amount, restored_quantity_base_units, restitution_target_base_units, recovery_audit_json, recovery_reversal_attempt |
| `FiatReversal.cs:226` | purchase_recovered_base_units |
| `OnRampLot.cs:163-192` | recovery_audit_json, environment, chain_network, refund_attempt, refunded_minor_units, reversed_base_units, reversal_marks_json |
| `WebhookRegistration.cs:18-21` | encrypted_secret, version, destination_revision, secret_revision (had no default) |

Sandbox evidence (`--force`, throwaway only): legacy `webhook_registration` row seeded under the
`f968955` schema → after this tree: `version 0, encrypted_secret ""`, `UPDATE … WHERE version = 0`
succeeds.

## `[Inside]` drift

None. Intentional exclusions kept (fail closed): `KycProvider.UNAVAILABLE` ("never persisted"),
`AtomicTransferGroupSubmissionState.NotSubmitted` (atomic-group row exists only after submission).

## Decisions needed (orchestrator / owner, for sweep P2)

1. **Checksum re-base.** 22 changed generated files abort `surrealforge up` on any database whose
   ledger holds the pre-W1 bytes (dev volumes, CI caches; Railway at `c6c55dd` already differs in
   ~80 files). Recommended: treat as a D31-style generation re-base (fresh DB: `dev-up -ResetDb`,
   Railway reset; greenfield, no customer data). P2 as written ("`up` the new schema over `main`'s
   without `--allow-destructive`") cannot pass; rescope it to a sandbox `--force` data-compat
   rehearsal, or choose baselines-plus-forward-migrations for all 22 files (widens the phase-3
   limit below). W1 did not freeze baselines.

## For W2 lanes

- **L1 `r4/platform`** (owns unlisted `docker-entrypoint.sh`, `.github/workflows/ci.yml`):
  phase-3 reconcile uses the *composed* directory as its desired model, so on every deploy it
  reverts migration-added constraints on baselined tables — today the `^[0-9a-f]{64}$` ASSERT on
  `operation_log.idempotency_key` (`Migrations/20260719_140000`). Options: reconcile against
  `Generated/Schemas` (needs a SurrealForge flag, owner repo), pass `--assembly`, or apply the
  composed set with `--no-reconcile`. Never add `--allow-destructive`: the reconcile reader parses
  `on_ramp_lot.surql` as empty (string-default parsing defect, owner repo) and would drop its fields.
- **Owner (SurrealForge):** reader defect above — minimal repro: `on_ramp_lot.surql` alone in a
  schemas dir, `up --dry-run` → 41 "remove" items; replacing `recovery_audit_json`'s `"[]"` default
  with a plain literal → 0.
- **L2:** `IdentomatScreeningService.RegisterMonitorAsync(..., lineageKey = null)` — a NONE
  `lineage_key` escapes UNIQUE `kyc_screening_monitor_query`; the only caller passes one, so make
  the parameter required.
- **L4:** `SurrealConsentGrantStore.UpsertAsync` assigns `Guid.NewGuid()` and upserts a grant
  outside the lifecycle transaction; it has no production caller — delete it so the
  command-id single-winner path is the only writer.
- **L3:** pre-W1 `fiat_payout` / `on_ramp_lot` rows read `environment = chain_network = "unbound"`;
  cash-out already refuses them (`OffRampService.cs:939`). New `platform_token_network` UNIQUE
  `(network)` fails index build only if two chains ever share a network row.
- **Every lane adding POCO fields in W2:** existing table → `option<>` or `[Default("ALWAYS …")]`
  (+ a backfill line if a CAS reads it); baselined table → forward migration in the same change;
  sweep P1 regenerates.

## Compile result

`dotnet build azoa.sln -c Debug` → **0 errors, 21 warnings** (budget 25; xUnit analyzer warnings in
test projects, none new from this lane).

## Checklists

- **Transaction scope** (`round3-transaction-scope-review.md`) per index decision: no store,
  batch, guard or result offset changed. Only index added is non-unique `kyc_submission_tenant_id`,
  already present on deployed DBs via `Migrations/20260718_131000`. `DEFAULT ALWAYS` changes write
  semantics only where a write sets NONE — no store `Unset`s or NONE-writes any of the 32 fields
  (grep). Backfill migration is idempotent (`WHERE … = NONE`, `??`).
- **§7 class list:** binding names / array captures / offsets / strict validator — no SurrealQL
  in C# touched; lowercase-enum and explicit-NULL raw writes — none (backfill writes defaults only);
  captured-null widening — n/a; bare ids into `[References]` — none; `;` inside `Of` — n/a;
  ordinal casts / converter-less enums — none added (§inside-audit clean); statements after
  top-level RETURN — none.
- **Pruning gate:** no new usings, no raw SurrealQL in C#, no catch-alls; terse code, rationale in
  `Persistence/SurrealDb/AGENTS.md` with a pointer from `Models/AGENTS.md`.
