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
- **No-throw.** Every method captures exceptions into an `AZOAResult<T>`.

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

- **No-throw.** Every method captures exceptions into an `AZOAResult<T>`; the manager
  treats any error result as "unconstrained" (fail-open — see the manager note).

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
