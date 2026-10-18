# Persistence/SurrealDb — schema generation, deployment path, and audits

Entity conventions live in [CONVENTION.md](CONVENTION.md); per-POCO design notes in
[Models/AGENTS.md](Models/AGENTS.md); migration authoring rules in
[Migrations/README.md](Migrations/README.md); baselines in
[CompatibilityBaselines/README.md](CompatibilityBaselines/README.md). This file records how the
generated schema reaches a database and the round-4 W1 audits (`r4/schema`).

## §generation

`Generated/Schemas/*.surql` (one per `[SurrealTable]` POCO, 131) and `Generated/Flowcharts/` are
emitted, never hand-edited. Regenerate with the golden tests in write mode:

```powershell
dotnet build azoa.sln -c Debug
$env:AZOA_REGENERATE_GOLDENS = "1"
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug --no-build `
  --filter "FullyQualifiedName~AttributePocoByteEquivalenceTests|FullyQualifiedName~FlowchartRegenerationTests"
```

A second run must leave the tree clean. POCO count must equal the schema file count (no orphan
goldens; `SchemaGoldenOrphanTests`).

## §deploy-path

`docker-entrypoint.sh schema` and the CI schema job compose `Generated/Schemas` **overlaid by**
`CompatibilityBaselines/` into one directory, then run `surrealforge up --no-reconcile`: phase 1
schema files, phase 2 `Migrations/` (lexical). Every file is checksummed in `schema_migration`; a
changed file aborts `up` (exit 2) unless `--force` (never passed).

**Why `--no-reconcile` (round 4 W2, platform lane).** Phase 3 reconcile diffs the live DB against
the *composed* directory, so for a baselined table the desired model is the frozen baseline and
every deploy reverted migration-added constraints (the `^[0-9a-f]{64}$` ASSERT on
`operation_log.idempotency_key`, `Migrations/20260719_140000`). SurrealForge 1.3.0 offers no flag
to reconcile against a different directory. `--assembly <AZOA.WebAPI.dll>` (POCO model) was probed
and rejected: it converges, but re-evolves three `node_fee_settlement` ASSERTs on every run (its
comparer reads `dec`/quote spelling as drift) and reports the `executes`/`forked_from` `in`/`out`
fields as removals, so the CI re-run gate can never pass. Probe (throwaway v3.1.4): composed +
`--no-reconcile` → `INFO FOR TABLE` identical to `Generated/Schemas` alone for all 131 tables;
second run `applied 0 … evolved 0`; composed + default reconcile → the one ASSERT difference.

Round 4 ships its schema by **generation reset** (owner decision 2026-10-17: `dev-up -ResetDb`
locally, Railway SurrealDB volume reset before the next deploy), so W1's changed generated files
need no baselines or forward migrations. Consequence after the reset: nothing evolves a shipped definition implicitly. Any change to a field or index on a
table that already exists on a deployed database ships as a forward migration with
`DEFINE … OVERWRITE` (the same rule §compatibility-baselines already imposes on baselined tables;
a changed generated file aborts `up` on checksum first anyway).

Gates: the CI schema job applies the composed set twice (second pass `applied 0 … evolved 0`), then
applies `Generated/Schemas` alone to `azoa_reference` and fails on any `INFO FOR TABLE` difference
("Deploy path converges to the generated model").

Known limits (SurrealForge, owner repo; harmless while nothing reconciles or passes
`--allow-destructive`):

- **The reconcile reader loses all of `on_ramp_lot`** (every field and index reads as absent from
  the desired model). Isolated to the file's bracket/brace string defaults (`"[]"`, `"{}"`):
  replacing `recovery_audit_json`'s `"[]"` with a plain literal makes it parse; escaping or
  re-quoting the `"{}"` default alone does not. Repro: `on_ramp_lot.surql` alone in a schemas dir,
  `up --dry-run` → 41 "remove" items. With `--allow-destructive` reconcile would drop its fields.
- **The `--assembly` reconcile comparer is not idempotent** (above).
- `DEFINE PARAM IF NOT EXISTS` never updates an `[Inside]` list on an existing database; an
  upgraded database needs a `DEFINE PARAM OVERWRITE` migration.

## §compatibility-baselines

A baselined table's generated golden is **not** what a deployment applies; only baseline +
migrations are. Every POCO change to `admin_bootstrap_state`, `bridge_tx`, `holon`,
`kyc_submission`, `operation_log` or `user` (`swap_state` was pruned in round 4) therefore needs a forward migration in the same
change. W1 found `kyc_submission` had drifted (Identomat missing from `$kyc_submission_provider`, so
every Identomat KYC write failed its ASSERT; `aml_lineage_key` absent) and added
`Migrations/20261017_121000`. `KycSubmission` gained the `kyc_submission_tenant_id` index that
`Migrations/20260718_131000` already created, so the golden and the deployed table agree. `user`
joined this list on 2026-10-18 (user-discoverable): its first post-generation-reset field addition
(`discoverable`) froze `CompatibilityBaselines/user.surql` and added
`Migrations/20261018_090000__add_user_discoverable`.

## §generation-rebase (W1)

W1 regenerated 23 existing goldens and added 61 new ones. Any database whose ledger recorded the
pre-W1 bytes (local dev volumes, CI caches, Railway at `c6c55dd`, which already differs in ~80
files) will abort `up` on checksum mismatch — verified by applying the `f968955` tree and then this
one: `checksum mismatch detected for 22 file(s)`. This is a generation re-base in the D31 sense
(wipe-and-reseed, no customer data): deployments start from a fresh database (`dev-up -ResetDb`,
Railway reset). Keeping in-place upgrades instead would mean freezing every changed file as a
baseline plus forward migrations, which also widens the phase-3 limit above — an owner decision,
recorded in the W1 lane record for sweep P2.

## §unique-index-audit

SurrealDB v3.1.4 semantics (probed): a record-id collision raises ``Database record `…` already
exists``; a UNIQUE index raises ``Database index `…` already contains …``; both satisfy
`SurrealIdempotencyLedger.IsUniqueViolation`. **A row whose indexed tuple contains NONE does not
participate in a UNIQUE index** (single or composite). An optional column inside a UNIQUE index is
therefore partial uniqueness by design, or a fail-open gap.

| Invariant | POCO / table | Enforced by |
|---|---|---|
| Ethereum nonce `(protocol, sender, nonce, nonce_slot)` | `EthereumPreparation` / `ethereum_preparation` | UNIQUE `ethereum_preparation_live_nonce` (all non-optional) + record id `EthereumIdentity(protocol, sender, key)`; stale `ethereum_preparation_nonce` removed by `Migrations/20261017_120000` |
| KYC person key per tenant (D23) | `KycIdentity` / `kyc_identity` | UNIQUE `kyc_identity_scope_person (tenant_scope, person_key)` + record id `KycPersonKeyDeriver.RecordId`; deliberately no global unique on `person_key` |
| Per-operation person claim (D3) | `PersonOperationClaim` | UNIQUE `person_operation_claim_unique` + record id `RecordIdFor(scope, op, person)`. Governance votes `UPSERT` the claim (re-vote by the same voter); the single winner per command is the `governance_command_receipt` record id |
| Hosted admission | `HostedSession` / `hosted_session` | UNIQUE `hosted_session_admission (flow_kind, provider_key, admission_key)` (non-optional; session ids are random). `hosted_session_provider_session` is partial until a provider session exists |
| Hosted provider attempt / callback | `HostedProviderAttempt`, `HostedProviderCallback` | attempt record id = session id; callback record id = digest(adapter, origin, event id); `hosted_provider_remote` partial until `remote_session_id` is set |
| KYB current authority | `KybVerification` | record id = tenant owner Guid |
| Active KYC submission per subject/tenant | `KycSubmission` | not an index: `KycEvidenceFence` guard row written in the admission transaction serializes concurrent admissions |
| Idempotency key | `IdempotencyKeyStore` | UNIQUE `idempotency_key_unique (key)` + record id |
| Fiat event id | `FiatEvent` | UNIQUE `fiat_event_provider_event` + record id `RecordIdFor(provider, event id)` |
| On-ramp lot per intent | `OnRampLot` | UNIQUE `on_ramp_lot_intent` + record id `RecordIdFor(intent)` |
| Payout per request | `FiatPayout` | UNIQUE `fiat_payout_request (subject, request_key, environment, chain_network)` (non-optional) + record id |
| Reversal per provider object | `FiatReversal` | UNIQUE `fiat_reversal_object` + record id |
| Clawback / purchase allocation receipt | `ExchangePurchaseAllocation` | record id `hash(lot, source)`; clawback idempotency keys via `idempotency_key_store` |
| WTT body / VAA | `WttBodyClaim`, `ConsumedVaaLedger` | UNIQUE `wtt_publication_identity` + record id `(partition, body digest)`; UNIQUE `consumed_vaa_digest`, `consumed_vaa_emitter_address_sequence` |
| WTT operation / stage key / preparation | `WttOperation`, `WttStageKeyEnvelope`, `WttPreparation` | record ids: `WttOperationIdentity.Operation(actor, tenant, key)`, content hash, preparation identity |
| Swap operation | `SwapOperationRecord` | record id `SwapOperationIdentity.Operation(actor, tenant, key)`; one swap per quote is a transactional CAS on `dex_quote.consumed_by_operation_id = NONE` |
| DEX quote | `DexQuoteRecord` | random id, immutable; no single-winner create (consumption CAS above) |
| Swap stage / DEX allowance | `SwapStageRecord`, `EthereumDexAllowance` | UNIQUE `swap_stage_identity`, `ethereum_dex_allowance_resource` |
| NFT lifecycle | `NftLifecycle`, `NftAssetAuthority` | record id `NftLifecycleIdentity.ForRequest(actor, op, key, tenant)`; UNIQUE `nft_authority_nft`, `nft_authority_asset` |
| Fungible creation | `FungibleCreationOperation`, `FungibleAssetAuthority`, attachment/cancellation | record id `FungibleCreationIdentity.OperationId`; UNIQUE `fungible_asset_identity`, `fungible_asset_operation`, `*_operation` |
| Hold command / custody liability | `HoldCommand`, `HoldLedger`, `HoldMandateUseReceipt` | record ids `HoldConsentIdentity.Id`, `HoldCommandIdentity.Hold` (chain, network, protocol, tenant, owner, hold id); UNIQUE `hold_ledger_hold` is partial for NONE tenant/protocol/owner, the record id covers those rows; UNIQUE `hold_mandate_use_operation` |
| Consent grant | `ConsentGrant`, `ConsentLifecycleCommandReceipt` | record ids from `ConsentLifecycleIdentity.CommandId(grantor, request)` in one transaction |
| Wallet address | `Wallet`, `User` | UNIQUE `wallet_user_chain_address (user, chain, address)` + deterministic wallet id; UNIQUE `user_auth_wallet` (partial: wallet-auth users only) |
| Platform unit per network | `PlatformToken` | UNIQUE `platform_token_scope (chain, network)` and `platform_token_network (network)` + record id |
| Reserve / treasury ledgers | `PlatformReserveLedger`, `ProjectTreasuryLedger`, `NodeTreasuryDestination` | UNIQUE indexes + record ids |
| Fee settlement / atomic group | `NodeFeeSettlement`, `NodeFeeAtomicGroup` | UNIQUE `node_fee_settlement_parent_operation`, `node_fee_atomic_group_settlement_unique` + record ids |
| AML monitor per query shape | `KycScreeningMonitor` | UNIQUE `kyc_screening_monitor_query`; partial when `lineage_key` is NONE (the only production caller always passes one) |
| Action run / node execution / run source | `ActionRun`, `ActionNodeExecution`, `ActionRunSource` | record ids + UNIQUE `action_node_execution_run_node`, `action_run_source_identity` |
| Deployment operation, signing authorization, emit/local receipts, recovery audits | respective POCOs | record ids from request-scoped hashes (`DeploymentIdentity.Operation`, `action-participant-authorization-v1`, operation ids) |
| Webhook registration per tenant | `WebhookRegistration` | UNIQUE `webhook_registration_tenant_unique` |

No invariant in the list relies on a unique violation with nothing behind it; W1 added no index.
Partial (NONE-escaping) indexes by design: `bridge_tx_*`, `holon_transfer_reservation`,
`operation_log_idempotency_key`, `user_tenant_extuser`,
`user_auth_wallet`, `hosted_session_provider_session`, `hosted_provider_remote`.

## §old-row-rule

A field added to a table that existed at the pre-round-1 schema (`bb512cf`) must not make an
existing row unwritable. Probed on v3.1.4: `TYPE int DEFAULT 0` does **not** protect an old row —
the next `UPDATE` fails `Expected int but found NONE`, because plain `DEFAULT` applies on create
only. `DEFAULT ALWAYS` fills NONE on every write and leaves set values alone. Treatments:

- `option<…>`: every other added field (85), including `hosted_session.*`, `hold_ledger.*`,
  `kyc_*.lineage_key`, `user.auth_identity_version`, `person_operation_claim.version`.
- `[Default("ALWAYS …")]` (W1 change, 32 fields): `version` on `action_node_execution`,
  `action_run`, `consent_grant`, both webhook-event tables and `webhook_registration`;
  `retry_generation`/`cycle_attempt_count` (both webhook-event tables);
  `action_node_execution.manual_advance_released`; `idempotency_key_store.preparation_generation`;
  `fiat_payout.{bank_payout_attempt, environment, chain_network, recovery_requested,
  recovery_reversal_amount, restored_quantity_base_units, restitution_target_base_units,
  recovery_audit_json, recovery_reversal_attempt}`; `fiat_reversal.purchase_recovered_base_units`;
  `on_ramp_lot.{recovery_audit_json, environment, chain_network, refund_attempt,
  refunded_minor_units, reversed_base_units, reversal_marks_json}`;
  `webhook_registration.{encrypted_secret, destination_revision, secret_revision}`.
  `environment`/`chain_network` default to `"unbound"`, which cash-out already refuses
  (`OffRampService` "needs authoritative environment classification") — fail closed.
- Changed: `webhook_registration.secret` was `string` (non-empty); it is now the optional legacy
  column (`[Column(Name = "secret")]` on `LegacySecret`; the generator names columns from the
  property, not `[JsonPropertyName]`). An empty `encrypted_secret` marks a legacy row for the
  store's migrate-on-write path.
- Indexes changed on existing tables (`fiat_payout_request`, `hold_ledger_hold`,
  `kyc_screening_monitor_query` widened; `platform_token_network`, `kyc_submission_tenant_id`
  added) need a `DEFINE INDEX OVERWRITE` migration on an upgraded database (nothing reconciles;
  §deploy-path); widening cannot collide on old rows (NONE in the new
  columns); `platform_token_network` would fail only if two chains shared a network row.
- `DEFAULT ALWAYS` keeps an old row writable, but a CAS predicate (`version = $expected`) still
  compares the stored NONE, so `Migrations/20261017_122000` materializes the 32 defaults once
  (no-op on a fresh database). Sandbox evidence on v3.1.4: a legacy `webhook_registration` row
  (`secret` only) seeded under the `f968955` schema, then this tree applied with `--force`
  (sandbox only): row reads `version 0, encrypted_secret ""`, and `UPDATE … WHERE version = 0`
  succeeds.
- Rule for new POCO fields on existing tables: `option<>` or `[Default("ALWAYS …")]` plus a
  backfill line when a CAS reads the field; plain `[Default]` only on tables created in the same
  change.

## §inside-audit

Every `[Inside]` list was compared with the C# enum or constant set it stores (nested `*Kind`
enums against their `Models/` domain enums; string columns against the constants/literals the
services write). No drift. Intentional exclusions: `KycProvider.UNAVAILABLE` ("never persisted")
and `AtomicTransferGroupSubmissionState.NotSubmitted` (the atomic-group row exists only for a
submitted group) — a write of either fails the ASSERT, closed.

## §migrations

- `20260821_120000__widen_operation_amount_and_stamp_value`: `type::is::number` →
  `type::is_number` (3.x rename; the 1.x/2.x spelling is a parse error and broke the CI schema job
  and E2E boot). Edited in place because it never applied anywhere: added after the Railway
  deploy (`c6c55dd`) and unparseable on every 3.x database.
- `20261017_120000__remove_stale_ethereum_preparation_nonce_index`: defensive
  `REMOVE INDEX IF EXISTS`; a no-op on an absent table or index (probed).
- `20261017_121000__converge_kyc_submission_identomat_and_aml_lineage`: §compatibility-baselines.
- `20261017_122000__backfill_old_row_counters`: §old-row-rule.
- `20261017_130000__add_operation_log_needs_operator_status`: D-3 `NeedsOperator` status param overwrite.
- `20261018_090000__add_user_discoverable`: user-discoverable (§compatibility-baselines below); `user`
  gains `discoverable` (`DEFAULT ALWAYS false`) plus a one-shot NONE backfill.

## §node-admin-tables

`node_admin`, `node_admin_credential`, `node_admin_role`, `node_admin_challenge`, `node_admin_enrollment`,
`node_admin_session`, `node_admin_audit`, `node_admin_audit_head`, `node_admin_root_state` and
`node_admin_directory` (track `node-admin-rbac-cli_20261018`) are new tables: no baseline, no migration, plain
`[Default]` is allowed (§old-row-rule applies to existing tables only). The POCO for `node_admin` is
`NodeAdminAccount` because `NodeAdmin` collides with the `Services.NodeAdmin` namespace. Design notes:
`Services/NodeAdmin/AGENTS.md` §storage.

## §ownership

POCO→lane ownership for W2 is round-4 plan §4.8; every one of the 131 tables matches one glob there.
