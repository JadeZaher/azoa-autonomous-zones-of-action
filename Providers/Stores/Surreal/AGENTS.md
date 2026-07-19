# Providers/Stores/Surreal — design notes

## §ecosystem-tree — SurrealEcosystemStore (D2)

`final-hardening-cutover` Phase D / D2. Backs `IEcosystemStore` (the STARODK
ecosystem tree — see `Managers/AGENTS.md §ecosystem-tree` for the feature). Two
tables: `ecosystem` (root, one per STARODK) and `ecosystem_node` (tree nodes).

- **Decorated-POCO interface, inline wire POCO storage.** The interface exposes
  the attributed `Persistence/SurrealDb/Models/{Ecosystem,EcosystemNode}` POCOs
  (they drive schema emit), but the store persists via **inline private wire
  POCOs** (`EcosystemPoco`/`EcosystemNodePoco`) whose FK columns are `string`
  record-links. This mirrors `SurrealConsentGrantStore`: the record<> FK wire
  form is `table:hex`, produced by `SurrealLink.ToLink(table, hex)` on write and
  stripped by `SurrealLink.FromLink` on read. The manager works in bare Guid('N')
  hex throughout; the store owns the link (de)coding.
- **`ref_id` is NOT a record-link.** `EcosystemNode.RefId` is a polymorphic
  reference (DappSeries OR star_odk, discriminated by `ref_kind`), so it is stored
  as bare hex, never `table:hex`. SurrealDB `record<>` is single-table; ownership
  is validated app-side in the manager, not by a schema FK.
- **Cascade delete.** `DeleteAsync(ecosystemId)` deletes child nodes first
  (`DELETE ... WHERE ecosystem_id = $eco`) then the root, so no orphan nodes
  dangle. There is no schema-level ON DELETE.
- **Expected outcomes are typed.** Not-found and recognized conflicts use
  `AZOAResult<T>`; unexpected executor/mapping failures bubble to the host
  boundary described below.

## §holon-type-registry — SurrealHolonTypeRegistryStore (F5)

`final-hardening-cutover` Phase C / F5. Backs the opt-in Holon AssetType registry
(`IHolonTypeRegistryStore`). See `Managers/AGENTS.md §holon-type-registry` for the
full feature rationale; this note covers only the persistence choices.

- **Natural key = record id.** The registered `AssetType` name is BOTH the
  `holon_type_registry` record id and a UNIQUE-indexed column
  (`holon_type_registry_asset_type_unique`). Lookups go straight through
  `SELECT * FROM type::record($_t, $_id)` with the raw AssetType string bound as
  `$_id`, so there is no scan and no secondary read. This mirrors
  `SurrealDataMigrationLedgerStore` (applied-once ledger), where the id IS the key.
  Re-registering the same type therefore *replaces* the row rather than duplicating
  it; the UNIQUE index is the backstop.

- **Direct-POCO store (no domain/POCO split).** Unlike `SurrealHolonStore` (which
  bridges a legacy `Models.Holon` domain type to an inline `HolonPoco`), this store
  reads/writes the attributed `Persistence/SurrealDb/Models/HolonType` POCO directly
  — there is no separate domain model to translate to, so a split would be pure
  ceremony. Writes go through `SurrealWriter.Upsert(poco)` (the same coercion-safe
  SET-based path the other stores use).

- **`Normalize` strips the table prefix.** A round-tripped record id comes back as
  `holon_type_registry:<asset_type>`; `Normalize` strips the `table:` prefix so
  callers always see the bare AssetType name in both `Id` and `AssetType`. It also
  backfills `AssetType` from the id if the column ever came back empty.

- **Registry failure policy.** The manager owns the explicit fail-open decision
  for unavailable registry data; the store does not flatten arbitrary exceptions
  into an error string.

- **Schema.** The `.surql` under `Generated/Schemas/holon_type_registry.surql` is
  emitted from the POCO by `AttributeSchemaScanner` + `SurqlEmitter` and gated by the
  byte-equivalence test. Never hand-edit it: change the POCO and regenerate
  (`AZOA_REGENERATE_GOLDENS=1` or the CLI). `surrealforge up` picks the new file up
  lexically with no manifest registration.

## quest_webhook_event outbox (final-hardening F3)

`SurrealQuestWebhookOutboxStore` is a column-for-column mirror of
`SurrealConsentWebhookOutboxStore`: CREATE a row, then a polling worker scans due rows
(`status='Pending' AND next_attempt_at<=now`, oldest first) and transitions them with
conditional single-winner `UPDATE ... WHERE status='Pending'` (`AffectedCount()==1` is
the arbiter). Status literals are bound params so the schema `ASSERT INSIDE [...]`
compares the same tokens. FK columns are record-links: `tenant_id→avatar`,
`run_id→quest_run`, `node_id→quest_node`, `quest_id→quest`. It is the GENERIC quest.emit
mirror of the consent outbox; see `Services/Webhooks/AGENTS.md §quest-webhook`.

## §surrealql-3x-wire-surface — SurrealDB 3.1.4 audit (final-hardening E3)

The SurrealQL every store emits is 3.x-clean (audited against the v3.1.4 cutover;
136 store integration tests green on the live `azoa-dev-surrealdb` v3.1.4 container).
Invariants to preserve when adding stores:

- **`type::record($_t, $_id)`** for id-scoped CRUD, **`type::table($_t)`** for table
  scans. NEVER `type::thing` — that is the retired 1.5.x name (3.x renamed it to
  `type::record`); there are zero occurrences left in the tree.
- **Record ids / links are bound as parameters, never interpolated.** Ids go through
  `SurrealId.ToSurrealId(guid)` (lowercase-hex string); FK/record-link fields go through
  `SurrealForge.Client`'s `SurrealLink.ToLink(table, id)`. 3.x accepts the `table:id`
  STRING form and REJECTS the 1.5.x `{tb, id}` object form on writes. The only place a
  `{tb,id}` object appears is `SurrealSagaStore`'s response *deserializer* — that is 3.x
  handing a RecordId back OUT, not us writing the object shape IN.
- **`INSIDE`** is the set-membership operator (`status INSIDE $_statuses`) — 3.x-valid,
  keep binding the set as a param so schema `ASSERT INSIDE [...]` compares the same tokens.
- **Multi-statement atomicity is one HTTP request.** 3.x scopes a transaction to a single
  `/rpc` request, so compose `BEGIN; …; COMMIT;` via `SurrealQuery.Combine(...)` (see
  `SurrealQuestRunStore` fork path) — never as separate stateless requests. RELATE cannot
  parse an inline `type::record(...)` on either side of the `->edge->` arrow, so bind both
  endpoints to `LET` vars first, then `RELATE $_child->forked_from->$_parent`.
- **SELECT hits only DEFINED tables.** 3.x errors on `SELECT ... FROM undefined_table`
  (1.5.x returned empty-OK), so the per-test namespace bootstrap and the boot schema apply
  must run before any store query — enforced by `SurrealTestSchema.BootstrapAsync` in tests.
- **No string-interpolated SurrealQL** (`.Of($"...")`): the `SurrealForge.Analyzer`
  SRDB0001 rule fails the build on interpolation, and the build is clean.

## Error boundary for new and modified stores

Store methods return `AZOAResult<T>` for expected persistence outcomes such as
not-found, validation rejection, or a recognized compare-and-set conflict.
Unexpected executor, protocol, mapping, and serialization exceptions must bubble
to the host boundary so the centralized logger receives their type, stack, query
context, and trace correlation. Do not add blanket `catch (Exception)` blocks
that reduce an unexpected failure to `ex.Message`; when touching an existing
method, prune that pattern if its callers already run behind a request, worker,
or CLI exception boundary.

Tenant identity/KYC stores are the narrow exception to the general bubbling
rule: their legacy `AZOAResult.Detail` path could serialize captured exceptions
in development. These stores log the full exception with trace correlation and
an internal entity id, then return fixed public `*_STORE_UNAVAILABLE` messages
without an attached exception. Tenant managers translate those once more to
fixed `TENANT_*_UNAVAILABLE` messages.

`SurrealKycStore.CreateSubmissionAsync` and `AttachDocumentsIfAbsentAsync` use
parameterized raw transactions because their invariants span an
active-submission CAS and a batch of document creates. Creation admits only one
active attempt. Attachment lets the first document set win, so concurrent or
replayed submissions read the existing set and never partially replace it.
Replace these seams when SurrealForge exposes conditional update plus looped
creates in one typed transaction.

`TryReviewAsync` is a parameterized conditional update: only an unexpired,
active manual attempt can accept one operator decision. A concurrent loser
returns no row and must not overwrite the winning terminal decision. Replace
this seam when a typed conditional-update builder is available.

## Typed mutation boundary

This directory documents datastore mechanics only. Domain policy belongs in the
owning manager/service guide. Prefer typed query/writer/mutation builders for
ordinary CRUD and typed relation builders for graph edges. DDL belongs in schema
generation. Retain raw statements only for a genuinely multi-table/multi-statement
atomic invariant. A temporarily unsupported single statement requires a linked,
expiring package issue/track waiver plus a one-line `raw:` reason. Colon-bearing scalar
strings must use the package's coercion-safe string binding; do not reimplement
encoding or character-splitting workarounds in individual stores.

Temporary waiver: `SurrealNodeTransparencyStore` uses two parameterized SELECT
variants for one descending `(occurred_at,id)` keyset pagination path because
the current typed read builder cannot express the required disjunction plus
record-id ordering. The active `azoa-code-style-backpropagation` track owns the
SurrealForge.Client remediation; the reviewed waiver expires 2026-09-30.

`SurrealKycControlStore` uses the same fully parameterized composite predicate
for its filtered operator audit timeline. The boolean first-page sentinel is
paired with valid dummy cursor values, keeping one stable query shape while the
exclusive `(occurred_at,id)` anchor prevents concurrent inserts from shifting
later pages. It shares the 2026-09-30 typed-builder waiver above.

Temporary waiver: `SurrealHolonStore` retains one delete and three conditional
transfer mutations until the tested SurrealForge.Client 0.4 mutation package is
published and consumed here. The same code-style track owns that migration; the
waiver expires 2026-08-31.

`SurrealNodeFeeSettlementStore` uses typed `SurrealQuery<T>.Key` for direct
record reads; it intentionally exposes no unpaired settlement-create method.
`AdmitAsync` has a temporary
2026-08-31 raw waiver because the consumed SurrealForge package cannot express
the one transaction that conditionally creates/replays an
`idempotency_key_store` parent and `node_fee_settlement` child across two
tables. It is bounded, fully parameterized, transient-conflict retried, and
returns only the final paired rows. Both missing means both are created; both
present means replay after parent key/operation validation; either one present
is a corruption error, never an implicit repair, except that a parent-only row
with a different ordinary operation is a known outer-ledger collision and is
returned as a fail-closed conflict without mutation. Admission accepts only a
fresh canonical `Prepared` row with both effects `NotStarted`, zero lifecycle
counters, and no links, hashes, lease, or reconciliation fields. The raw
`CONTENT` dictionary is an explicit allowlist that writes only the immutable
economics (canonical positive `ulong` base-unit strings with overflow-safe
`gross = fee + net`) and those canonical inert lifecycle values, so a caller
cannot smuggle record links, an unbalanced amount, or an effect state through
it. New parents are always `InProgress` without result/error. Parent and
settlement terminality must agree on replay;
neither terminal-before-nonterminal direction is accepted. Parent-key trimming
is centralized before both hash and deterministic record-id derivation, and its
colon-safe stored form matches the configured package ledger adapter. The
recovery read and its two lease mutations
share the same expiry because the consumed package lowercases enum predicates
and lacks a typed multi-field conditional builder for the claim's ORed
due-or-expired predicate and the defer's exact token-plus-expiry guard. Those
parameterized statements carry `raw:` pointers in code; replace all six raw
seams with typed conditional/transaction builders as soon as the packaged
surface lands. Do not classify duplicate races from server error-message text.
Recovery scan rows are only candidates: claim always repeats the state-version
plus due-or-expired lease predicate, and every reconciliation or terminal write
requires the exact unexpired lease token. A nonterminal effect report must contain
at least one `Unknown`/`Failed` state and cannot alter the parent. Confirmed
effect references are monotonic: later reconciliation or terminalization must
preserve the exact observed reference, enforced inside the same lease CAS.
The raw reconciliation statement also explicitly casts optional transaction
references through `SurrealScalarString`, preserving colon-bearing chain
identifiers rather than allowing SurrealDB to coerce them into record ids.
`TrySettlePairedAsync`
is a second bounded, parameterized raw transaction (same 2026-08-31 waiver): it
accepts two distinct confirmed references and atomically changes the settlement
to `Settled` and its matching `InProgress` parent to `Completed`. It never
submits, signs, or polls an effect, and stale/illegal/reverse transitions return
no mutation. Value-path lifecycle activation still requires reviewed
provider/reconciliation hand-offs with live-SurrealDB concurrency tests.
Within that transaction, `UPDATE ONLY ... RETURN AFTER` is already one object on
SurrealDB 3.x; do not append `.first()`, which aborts the transaction.

`GetAcceptedAtomicGroupAsync` is deliberately ordinary typed record access by
the receipt's deterministic settlement-derived id. It validates the receipt's
stored settlement link before returning it; no raw join or scan is permitted for
this one-to-one immutable read. Paired terminalization accepts either the legacy
raw parent key or its already-persisted SHA-256 parent record id. Hash-only
callers retain the parent id, `InProgress` state, and operation checks inside the
same transaction, but cannot reconstruct or expose the raw idempotency key.

Receipt-driven reconciliation uses a separate lease CAS that requires the
deterministic receipt record to exist inside the same conditional update. This
prevents the observation loop from changing an ordinary `Prepared` or
non-atomic settlement before it has an accepted receipt; a no-receipt candidate
returns ordinary claim contention with no state, lease, or attempt mutation.
The receipt-existence predicate is one additional narrow raw waiver under the
same 2026-08-31 typed-builder limitation, not permission for raw receipt reads.

`TryRecordAcceptedAtomicGroupAsync` is a third bounded raw transaction under
the same 2026-08-31 waiver. It conditionally creates the one-to-one immutable
`node_fee_atomic_group` receipt while the exact unexpired settlement lease and
economic binding still match, sets both recorded effect references to Submitted,
and releases the lease into immediate reconciliation. Exact receipt replay is
idempotent; divergent evidence conflicts. This closes only the post-broadcast
receipt gap as far as durable evidence permits: a crash before the receipt is
written remains ambiguous and must not trigger a re-broadcast.
`SurrealQuery.Of` accepts one statement only, so the six receipt statements are
composed by `SurrealQuery.Combine` into one HTTP transaction; do not replace
that with a semicolon-joined `.Of` body. The architecture ratchet counts those
fragments but treats them as this one reviewed 2026-08-31 receipt waiver.

## §set-omits-null — always serialize option<> collections (Phase H / H7)

`SurrealWriter.Upsert` builds a SET-based `UPDATE ... SET col = ...` and OMITS
null `option<>` fields. A mapping that only serializes a collection when
non-empty (`X = list.Count > 0 ? ... : null`) therefore makes "empty the
collection" a silent no-op: the null is dropped from the SET clause and the
stale stored value survives. Found in Holon (Phase H), then confirmed by audit
in Star (`bound_holon_ids`), Nft AvatarNFT (`attributes`), and
BlockchainOperation (`parameters`) — all fixed 2026-07-06.

Rules for any SET-upserted store mapping:
- ALWAYS serialize collection/dictionary props via
  `JsonSerializer.SerializeToElement(...)` (BCL, .NET 6+) — empty emits
  `[]`/`{}` and REPLACES the column.
- Null-on-empty is only acceptable for genuinely-optional SCALAR fields
  (e.g. an optional record link) where null means "absent", never for
  clearable collections.
- CONTENT-based writes (`CREATE/UPSERT ... CONTENT $_body`) are full-document
  replaces and immune; this rule applies to `SurrealWriter.Upsert` paths only.
