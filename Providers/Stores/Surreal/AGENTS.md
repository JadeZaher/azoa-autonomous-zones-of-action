# Providers/Stores/Surreal â€” design notes

## Fee policy audit transaction

Node fee updates use typed Create or expected-version UpdateOnly plus typed audit
creation. The raw transaction captures the typed mutation, rejects a missing CAS
result and commits its audit together. Parameters are independently scoped. This
also persists the explicit swap denomination without upgrading legacy schedules
implicitly. A concurrency loser produces no orphan audit or fabricated success.

## Swap stage authority

The initial stage plan is claimed with its owning operation epoch before custody.
Saving a canonical validated signature fills one immutable candidate and does not
authorize egress. First-send authorization and no-send cancellation update both
operation and stage in one transaction; operation/stage CAS failure rolls back
all writes. Positive authority joins current signer/action fences; cancellation
never creates spending authority. Exact authorized replay retains original bytes
after credential expiry, but still validates actor/context before exposing them.
Provider validation of complete wire and authoritative settlement are separate
mandatory boundaries. The initial stage store does not yet implement subsequent
stage advancement, no-effect replacement, economic completion or cleanup.

## WTT protected stage keys

`SurrealWttStageKeyStore` uses typed keyed create/read and exact-envelope version CAS. The record key hashes canonical binding JSON; a duplicate creation returns the original envelope even if the candidate contains a different random seed/public key. Only a wrapping update can change ciphertext/version; the public key and binding remain immutable. Readiness uses a bounded negative key-version membership query so a missing retained encryption version prevents activation/retirement. No raw query exception is needed.

## DEX immutable quotes

`SurrealDexQuoteStore` uses typed keyed create/read for immutable server-issued
quotes. Canonical JSON/hash and actor/tenant/expiry columns must agree. Only a
recognized unique conflict is reread; exact replay preserves the original quote
and any later consumption link. This store does not consume a quote or authorize
egress: consumption belongs in the swap operation's atomic admission transaction.
Protocol and economic validation precede quote creation in the service/provider.

`SurrealSwapOperationStore` atomically consumes a quote and creates the original
request. Its multi-record raw guards use database time for quote/credential/grant
expiry and serialize current user, wallet and explicit consent rows with the
typed operation write. `ActionExecutionFence` joins the same transaction for
action-driven admission. Generated mutation parameters are scoped before flat
composition. Identical replay reads the original result before mutable admission
checks; it never creates a second economic decision.

`DexSigningAuthorityFence` accepts a server-derived authenticated-principal and
wallet/grant snapshot, not a client body. Positive admission requires the exact
actor/tenant, active owner, live authentication watermark, current wallet binding,
active tenant and a live UserExplicit grant covering swap:sign. Direct owner
cancellation of a provably unprepared request needs only current owner credentials:
deleting a wallet or revoking a tenant grant must not strand a no-send tombstone.
A tenant caller still needs its original live grant. Cancellation never signs or
submits and cannot touch an operation with any current stage. Stage creation must
CAS this same operation version/state before preparation. The cancellation
tombstone uses the same request identity, so delayed original admission cannot
revive it. A consumed quote is not released for a second operation.

## Ethereum preparation ownership

`SurrealEthereumPreparationStore` uses typed keyed reads, creates and version CAS.
Its immutable protocol/sender/preparation identity and unique fixed-width nonce
prevent cross-host signer collisions. Recovery updates cannot modify the admitted
call, actor, nonce or activation epoch. Signed wire is separate from submission
authority; only `SubmissionAuthorized` rows enter the fair oldest-attempt sweep.
Checkpoints are create-only and never silently relabeled to a new deployment epoch.

Released-nonce refill: `ListReleasedAsync` returns the lowest `Released` rows for
one protocol/sender. `TryClaimReleasedAsync` claims one released nonce for a new
preparation in a single raw transaction (`// raw:` retire-and-create must share
one commit).

1. A typed `UpdateOnly` turns the released row into a `ReleasedRefilled`
   tombstone with `nonce_slot = its own id`. It is captured as
   `$released_nonce` and fenced on version, `Released` state, exact nonce and
   an empty slot. `RequireResult` aborts with `ETHEREUM_RELEASED_NONCE_CONFLICT`
   otherwise.
2. A typed `Create` of the successor follows, returned by name
   (`$released_successor`).

The row is never deleted, so its id keeps the preparation-key lock forever.

The unique index is `ethereum_preparation_live_nonce`
`(protocol_identity, sender, nonce, nonce_slot)`. Live rows have an empty slot,
so there is still one live owner per nonce. Each tombstone's slot is unique, so
tombstones never collide.

The batch runs under `SurrealTransientConflict.RetryOnConflictAsync`. A
concurrent refiller loses on the marker or the unique index and retries through
the allocator loop. Only never-signed rows reach `Released`; see
`Providers/Blockchain/Ethereum/AGENTS.md` §Nonce holes.

Client-signed reservations (RV-L5-01) share the table and the live-nonce index: `State = ClientReserved`, empty owner,
`IntentJson = "{}"`, never signed, released or recovered by custody. They are claimed with the same `TryClaimAsync`
(create; a unique violation on the id or the live nonce returns the existing row or null), so one live owner per
(protocol, sender, nonce) still holds across custody and client-signed plans. See
`Providers/Blockchain/Ethereum/AGENTS.md` §Client-signed nonces.

Deploy note: the index was renamed, so the schema sync (`IF NOT EXISTS`) defines
the new one. A database that still has the old `ethereum_preparation_nonce`
index must drop it (dev: `-ResetDb`). Until then, refills fail closed as "busy"
instead of double-assigning.

## Â§ecosystem-tree â€” SurrealEcosystemStore (D2)

`final-hardening-cutover` Phase D / D2. Backs `IEcosystemStore` (the ActionGraphBundle
ecosystem tree â€” see `Managers/AGENTS.md Â§ecosystem-tree` for the feature). Two
tables: `ecosystem` (root, one per ActionGraphBundle) and `ecosystem_node` (tree nodes).

- **Decorated-POCO interface, inline wire POCO storage.** The interface exposes
  the attributed `Persistence/SurrealDb/Models/{Ecosystem,EcosystemNode}` POCOs
  (they drive schema emit), but the store persists via **inline private wire
  POCOs** (`EcosystemPoco`/`EcosystemNodePoco`) whose FK columns are `string`
  record-links. This mirrors `SurrealConsentGrantStore`: the record<> FK wire
  form is `table:hex`, produced by `SurrealLink.ToLink(table, hex)` on write and
  stripped by `SurrealLink.FromLink` on read. The manager works in bare Guid('N')
  hex throughout; the store owns the link (de)coding.
- **`ref_id` is NOT a record-link.** `EcosystemNode.RefId` is a polymorphic
  reference (DappSeries OR action_graph_bundle, discriminated by `ref_kind`), so it is stored
  as bare hex, never `table:hex`. SurrealDB `record<>` is single-table; ownership
  is validated app-side in the manager, not by a schema FK.
- **Cascade delete.** `DeleteAsync(ecosystemId)` deletes child nodes first
  (`DELETE ... WHERE ecosystem_id = $eco`) then the root, so no orphan nodes
  dangle. There is no schema-level ON DELETE.
- **Expected outcomes are typed.** Not-found and recognized conflicts use
  `AZOAResult<T>`; unexpected executor/mapping failures bubble to the host
  boundary described below.

## Â§holon-type-registry â€” SurrealHolonTypeRegistryStore (F5)

`final-hardening-cutover` Phase C / F5. Backs the opt-in Holon AssetType registry
(`IHolonTypeRegistryStore`). See `Managers/AGENTS.md Â§holon-type-registry` for the
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
  â€” there is no separate domain model to translate to, so a split would be pure
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

## action_webhook_event outbox (final-hardening F3)

`SurrealActionWebhookOutboxStore` is a column-for-column mirror of
`SurrealConsentWebhookOutboxStore`: CREATE a row, then a polling worker scans due rows
(`status='Pending' AND next_attempt_at<=now`, oldest first) and transitions them with
conditional single-winner `UPDATE ... WHERE status='Pending'` (`AffectedCount()==1` is
the arbiter). Status literals are bound params so the schema `ASSERT INSIDE [...]`
compares the same tokens. FK columns are record-links: `tenant_idâ†’user`,
`run_idâ†’action_run`, `node_idâ†’action_node`, `action_idâ†’action`. It is the GENERIC action.emit
mirror of the consent outbox; see `Services/Webhooks/AGENTS.md Â§action-webhook`.

## Â§surrealql-3x-wire-surface â€” SurrealDB 3.1.4 audit (final-hardening E3)

The SurrealQL every store emits is 3.x-clean (audited against the v3.1.4 cutover;
136 store integration tests green on the live `azoa-dev-surrealdb` v3.1.4 container).
Invariants to preserve when adding stores:

- **`type::record($_t, $_id)`** for id-scoped CRUD, **`type::table($_t)`** for table
  scans. NEVER `type::thing` â€” that is the retired 1.5.x name (3.x renamed it to
  `type::record`); there are zero occurrences left in the tree.
- **Record ids / links are bound as parameters, never interpolated.** Ids go through
  `SurrealId.ToSurrealId(guid)` (lowercase-hex string); FK/record-link fields go through
  `SurrealForge.Client`'s `SurrealLink.ToLink(table, id)`. 3.x accepts the `table:id`
  STRING form and REJECTS the 1.5.x `{tb, id}` object form on writes. The only place a
  `{tb,id}` object appears is `SurrealSagaStore`'s response *deserializer* â€” that is 3.x
  handing a RecordId back OUT, not us writing the object shape IN.
- **`INSIDE`** is the set-membership operator (`status INSIDE $_statuses`) â€” 3.x-valid,
  keep binding the set as a param so schema `ASSERT INSIDE [...]` compares the same tokens.
- **Multi-statement atomicity is one HTTP request.** 3.x scopes a transaction to a single
  `/rpc` request, so compose `BEGIN; â€¦; COMMIT;` via `SurrealQuery.Combine(...)` (see
  `SurrealActionRunStore` fork path) â€” never as separate stateless requests. RELATE cannot
  parse an inline `type::record(...)` on either side of the `->edge->` arrow, so bind both
  endpoints to `LET` vars first, then `RELATE $_child->forked_from->$_parent`.
- **SELECT hits only DEFINED tables.** 3.x errors on `SELECT ... FROM undefined_table`
  (1.5.x returned empty-OK), so the per-test namespace bootstrap and the boot schema apply
  must run before any store query â€” enforced by `SurrealTestSchema.BootstrapAsync` in tests.
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

## Â§set-omits-null â€” always serialize option<> collections (Phase H / H7)

`SurrealWriter.Upsert` builds a SET-based `UPDATE ... SET col = ...` and OMITS
null `option<>` fields. A mapping that only serializes a collection when
non-empty (`X = list.Count > 0 ? ... : null`) therefore makes "empty the
collection" a silent no-op: the null is dropped from the SET clause and the
stale stored value survives. Found in Holon (Phase H), then confirmed by audit
in ActionGraphBundle (`bound_holon_ids`), Nft UserNFT (`attributes`), and
BlockchainOperation (`parameters`) â€” all fixed 2026-07-06.

Rules for any SET-upserted store mapping:
- ALWAYS serialize collection/dictionary props via
  `JsonSerializer.SerializeToElement(...)` (BCL, .NET 6+) â€” empty emits
  `[]`/`{}` and REPLACES the column.
- Null-on-empty is only acceptable for genuinely-optional SCALAR fields
  (e.g. an optional record link) where null means "absent", never for
  clearable collections.
- CONTENT-based writes (`CREATE/UPSERT ... CONTENT $_body`) are full-document
  replaces and immune; this rule applies to `SurrealWriter.Upsert` paths only.

## Â§person-operation-claim-store â€” SurrealPersonOperationClaimStore (D3/D23)

The Sybil ledger behind `IPersonUniquenessGate`. Two shapes carry its correctness and
neither is stylistic.

**The winner is decided inside ONE statement, then read back.** The record id is
deterministic over `(scope, operation, personKey)`, and the claim statement both writes
and returns the row; the caller compares the returned `UserId` to its own to learn
whether it won. A `SELECT` before the write is the bug this avoids â€” two concurrent
claims both pass a preceding read and both proceed. This mirrors the `BindUserAsync`
precedent in `SurrealKycIdentityStore`; copy that shape for any single-winner claim.
(A losing concurrent write can still surface as a retryable transaction conflict on
RocksDB â€” see `Core/Surreal/AGENTS.md`.)

**Reads cannot become a person-key oracle.** `ListLiveByOperationAsync` is keyed by
`(scope, operation)` only â€” a caller cannot ask this store about a person it names. A
store fault must surface as an error result and never as an empty list, because an empty
list is indistinguishable from a published tally of zero. This store is
node-operator-only: no consumer route may reach it, and the global person key exists
nowhere else on a consumer-readable surface.

Column semantics and the uniqueness/index rules are in
`Persistence/SurrealDb/Models/AGENTS.md` Â§person-operation-claim; the policy above it is
in `Services/Kyc/AGENTS.md` Â§person-uniqueness.

## Â§issuer-column â€” the one field that is NOT Â§set-omits-null (D19)

`SurrealNftStore` maps `IUserNFT` â†” `NftOwnership` field-by-field, but `issuer_user_id`
is read and written through a second, narrow face on the same store,
`ICredentialIssuerStore` â€” never through the ordinary `IUserNFT` upsert. `IUserNFT` has
no issuer property, and its only writable carrier is the caller-supplied `Attributes`
bag, so routing the issuer through the normal mapping would let a holder name themselves
issuer and revoke their own credential. The issuer is the field the revoke authorization
decision turns on.

`ToNftPoco` therefore leaves `IssuerUserId` **null on purpose**, and here the
Â§set-omits-null behaviour is load-bearing rather than a trap: `SurrealWriter` drops null
`option<>` fields from the SET, so an ordinary transfer or metadata edit PRESERVES the
recorded issuer instead of wiping the authorization anchor. Read Â§set-omits-null's
"always serialize" rule as applying to clearable COLLECTIONS; this scalar is the
documented exception, and writing it unconditionally would erase revocability on every
unrelated update.

`SetIssuerUserIdAsync` is write-once (a differing issuer is refused, the same issuer
re-stamps cleanly so an idempotent mint retry succeeds) and
`RecordCredentialRevokedAsync` moves ownership only after a CONFIRMED clawback â€” without
it, AZOA keeps reporting a revoked credential as held. Pinned by
`tests/AZOA.WebAPI.Tests/Providers/Stores/Surreal/SurrealNftStoreCredentialIssuerTests.cs`.

**Known defect (why the rail is gated).** The production mint path stamps the issuer as
the HOLDER, which makes the "issuer, never the holder" authorization vacuous. That is why
`CredentialRevoke` sits on `Services/Actions/NotEnabledActionNodeTypes` â€” see
`Services/Actions/AGENTS.md` Â§not-enabled-node-types â€” and it is what
`conductor/tracks/credential-third-party-issuance_20260906` exists to fix.

## Hosted resumption and KYC completion

Hosted admission uses typed create with the unique admission index as arbiter and
only recognizes an actual unique violation as a contender race. All hosted reads
and mutations use typed builders. Lease claims require an eligible state and exact
version; renewal and finish additionally require the random lease token and live
expiry. Callback counter updates compare the prior count without changing the
lifecycle version. Unexpected failures propagate to the host boundary.

`TryCompleteHostedAsync` changes only outcome fields of the exact unexpired
Identomat attempt. Its typed predicate binds owner, tenant, provider/session,
policy provenance, original status, modification time and expiry. It never creates
a missing attempt or rewrites the trust envelope from a stale callback snapshot.

KYC completion also write-locks the `hosted_session` row in the same transaction
and requires its current RESUMING token and server-time lease expiry before the
typed attempt mutation. This multi-table atomic invariant is the reason for the
small raw transaction guard. A paused previous owner cannot commit an approval
after another process takes over, even if cancellation has not yet been observed.

## KYB atomic decision

`SurrealKybVerificationStore` uses typed keys, writers and conditional mutations for normal
reads/admission/session attachment. Its only raw transaction write-locks the hosted session
under the exact live resumption lease token/version/expiry and applies the tenant KYB CAS in
that transaction. This multi-table invariant prevents an expired worker from approving after
another worker takes over. See `Services/Kyb/AGENTS.md` for current-attempt and policy binding.

`BindVerifiedUserAsync` is a genuine three-record atomic invariant: write-lock the
live hosted lease, write-lock the exact unexpired active KYC attempt, then bind
identity membership. A lease/authority miss aborts the whole transaction with an
explicit conflict. The conditional identity upsert is retained only inside this
transaction; the standalone binding path uses typed create and conditional update.

## AML persistence

Screening rows, monitor reads/writes and poll state use typed queries and writers.
Poll state changes compare the observed modification stamp, failure count and
watermark; conflicting updates reload before applying, preserving concurrent
failure increments. Unexpected persistence exceptions reach the worker/HTTP
boundary. Cross-table subject erasure and bounded retention selection plus erasure
retain raw transactions so neither retained PII table can be cleared independently.

KYC ordinary reads, effective-status paging/counts, manual review CAS, and document writes now use typed queries/LINQ/writers. Effective-status display captures the application UTC clock per query; authority-bearing hosted completion additionally checks expiry against database time inside its transaction. Only create/attach and fenced domain completion retain raw multi-record transactions. Infrastructure exceptions reach the centralized boundary; only recognized authority/active-attempt conflicts are translated. Nullable mutation assignments use explicit Unset because TypedUpdateOnlyBuilder.Set rejects null.

## Round 1 hosted recovery transactions

Send-start fences current KYC attempt and tenant authority; alias attachment commits
its typed session CAS and deterministic alias reservations atomically. Absence
quarantine retains the admission and creator token; only provably unsent admissions
release. Reconciliation audit entries survive later attachment. All ordinary new
retirement/redirect writes are typed CAS. AML erasure transactions use flat Combine
statements, retaining bounds, ordering and both-table erasure; result indices count
BEGIN/LET slots. Tenant, provider and attempt guard parameters are distinct because
Combine does not rename bindings.

## NFT lifecycle atomic boundaries

`SurrealNftLifecycleStore` uses typed keys, filters, writers and conditional mutations for ordinary access. Three retained raw multi-record transactions enforce invariants unavailable as independent statements: reserve the exact live lifecycle and current authority version together; commit confirmed lifecycle, unique canonical asset authority and ownership projection together; cancel a never-dispatched/no-effect operation and release its reservation together. The asset identity has a composite unique index across chain, network, protocol, contract and asset identifier. Confirmed-stage count forbids cancellation after any prior effect while archived no-effect generations remain cancellable. Cancellation tombstones compete through the same typed Create admission key, scoped by tenant, actor and operation.

Combined typed writers have parameter names scoped by `SurrealGeneratedStatementScope`; SurrealForge Combine otherwise merges duplicate `_t`, `_id` and field parameters. Nullable ordinary CAS fields use Unset. Raw transaction comments point here; only named concurrency conflicts are translated, and infrastructure exceptions retain their original boundary. Real integration fixtures exercise writer parameter isolation, stale completion rollback, reservation release and concurrent admission.

## Tenant-principal query waiver

The pre-existing bounded `SurrealUserStore.ListTenantPrincipalsPageAsync` correlated API-key authority query cannot be expressed by the pinned typed query translator. Its precise gap, preserved authorization predicates, owner and 2026-11-15 expiry are recorded in [the active track waiver](../../../conductor/tracks/platform-stub-closeout_20261015/round2-typed-query-waiver.md). No other ordinary user CRUD retains raw queries. Wallet challenge admission reserves one of five versioned durable slots before creation; unsent reservations expire naturally, consumed slots may be reused, and pre-migration live challenges are counted. See `Services/Auth/AGENTS.md` for migration readiness and credential CAS invariants.

## Local action and node-policy fences

SurrealNodeGovernanceStore holds NodeGovernancePolicyFence.Lock while checking its
expected policy version and writing policy/audit. Ordinary policy and audit writes
use scoped typed builders; retained raw statements express the cross-record atomic
version invariant. Local action effects acquire the same lock before committing
validated plans, so an absent policy is fenced against concurrent initial insertion.

## Deployment action admission

SurrealDeploymentStore composes ActionExecutionFence with a scoped typed domain
mutation inside one transaction for action admission, first preparation ownership,
runtime activation and initial submission authority. These retained raw BEGIN/COMMIT
statements express the cross-record cancellation/fork invariant. Ordinary artifact,
quote, plan and journal CRUD remains typed. Native preparation plans are create-only;
their exact signed candidate can be filled once but cannot authorize broadcasting.

## Consumer election atomic commands

SurrealGovernanceStore commits authority, public projection and command receipts together. Its raw statements express cross-table authority and cutoff invariants; ordinary writes use scoped typed builders. Voting and finalization serialize through the proposal version. ActionExecutionFence serializes effects with cancellation and NodeGovernancePolicyFence protects creation policy validation. See Services/Governance/AGENTS.md for the complete election contract.

## claim-conditional-writer

Person-operation claims now use typed Create/UpdateOnly with optional Version CAS and PersonOperationClaimFence's atomic database-clock guard. Snapshot reads only construct the mutation; the transaction validates exact state, and a zero-row CAS retries or returns Conflict. The first decision cutoff remains fixed through release/reclaim, and no-decision reacquisition preserves prior decision fields. Literal NULL authority fields fail closed; legacy NONE Version advances to 1. The former [conditional-writer waiver](../../../../conductor/tracks/platform-stub-closeout_20261015/claim-conditional-writer.md) is retired following independent source acceptance; final integrated runtime verification remains mandatory.

## Current identity evidence transactions

KycEvidenceFence shares the existing subject/tenant attempt guard across KYC,
identity binding and AML mutations. See Helpers/AGENTS.md and the active closeout
track's round2-evidence-fence.md. Retained raw guards are part of the atomic
multi-record evidence/decision invariant; ordinary per-row writes remain typed.
HostedProviderAttempt stores exact normalized request bytes before egress and
uses a current-attempt-fenced lease for creation/replay. Callback receipts retain
only identity and a body digest. First terminal evidence identity is pinned on
the hosted row; contradictory evidence permanently fences new completion through
HostedSessionLeaseFence without rewriting an already accepted domain outcome.


## Atomic local action mutation plans

SurrealActionLocalMutationStore retains raw statements only for the multi-record atomic
invariant: execution/policy/current consent fences, exact row and query membership witnesses,
all typed staged mutations, and immutable receipt. Generated writers are individually scoped
before combining. Missing read witnesses and reserved election projections are refused.
Failed receipts carry no effects and still fence the admitted execution. Separate live
consent grants may jointly satisfy action and wallet scopes; relevant grants are locked.
Holon and wallet writers join LocalEntityMutationFence so membership reads cannot miss
concurrent insert/delete phantoms. Nullable clears use typed Unset. Malformed stored holon
metadata or peer arrays now propagate their parsing error instead of becoming empty state.


### Consumer governance transaction composition

Governance authority checks, person decisions, public projections and command receipts share one flat transaction. Each raw LET/IF/UPDATE is a separate `SurrealQuery.Of`; `Combine` receives the flat list, including shared policy/action/evidence guards. Parameter bindings are collected once per group and must agree when a name is shared. Ordinary row persistence remains typed and scoped with `SurrealGeneratedStatementScope`. See the active track's `round2-governance-independent-review.md` for the construction correction and pending integrated database verification.

## Action recovery transaction

`SurrealActionRecoveryStore` uses raw LET/IF only to coordinate original run/node authority and the exact parked saga generation with typed execution/saga updates and immutable audit creation. All statements are flat inputs to one Combine transaction. Stored saga keys use the shared SagaOpaqueKey codec; payload is preserved, while its original admission hash is checked separately so prior signals survive. No generic ordinary CRUD escape hatch is introduced.

## Action scheduler epoch and NFT workflow authority

ActionNodeExecution updates lock and increment their owning run version in the same flat
transaction as the typed node CAS. Initial admission additionally checks the scheduler's
observed Running run version; this serializes sibling failure, compensation and cancellation
with new effects. Ordinary progress CAS keeps immutable frozen input/reference unchanged.

NFT lifecycle workflow claims, preparation authorization and first Submitted transition
join ActionExecutionFence from the immutable command authority. Progress observation of
already admitted wire does not re-authorize a new effect. RequestJson and RequestHash remain
exact CAS guards. Known authority conflict translates to a typed refusal; infrastructure
exceptions propagate. Scoped typed writes preserve disjoint statement parameters.

## Linked native recovery
SurrealDeploymentStore.LinkedRecovery uses one multi-record transaction to bind a resolved parent snapshot (version, generation, immutable request and exact history) to a unique recovery child. Ordinary parent CAS cannot mutate a linked parent; child mutation locks and checks the live parent link, owner/tenant and binding in the same transaction. Service-level eligibility must separately prove every authorized original wire final or unable to execute; absence of PreparedJson alone is not chain evidence. Direct CAS returns its own mutation result rather than rereading a possibly competing update. Completion/replacement are separate required atomic transitions.

## Fungible creation and reserve registration

`SurrealFungibleCreationStore` retains raw statements only for atomic request/quote admission with current actor/action/policy authority and for the multi-table confirmed-issuance projection. Each statement is flattened before Combine and each typed writer receives a unique parameter scope. Completion creates immutable asset authority, optional reserve registration and optional owned unbound Holon attachment in one transaction; no independent-write fallback exists.

`SurrealPlatformReserveLedgerStore` now lives in this directory. Ordinary lookup/create/version mutation uses typed builders. Only recognized unique-create conflicts are reread; infrastructure failures bubble. Partition mutations cannot overwrite immutable registration identity, making version locks meaningful for issuance projection.

Recovery replacement joins typed predecessor CAS, parent CAS, exact quote lock and
new child creation in one transaction. The predecessor seal is permanent; ordinary
stage updates refuse it. Never-authorized retirement requires initial generation,
empty history and pre-submission state. Resolved failures require provider reproof
in the service before admission; the store pins that exact history/version against
concurrent mutation. The parent child link is swapped directly and never cleared,
preserving withdrawal/nonadmission proofs. Retained signed candidates and economic
history remain audit evidence and cannot regain submission authority after sealing.

WTT body reservations are immutable ownership, never settlement or consumed status. Their primary identity uses canonical double-Keccak body digest and stable target genesis/core/token-bridge partition; implementation upgrades and alternate guardian envelopes do not create another redemption identity. The unique emitter-chain/address/full-width sequence tuple additionally rejects conflicting bodies in that target partition. Exact operation/actor/tenant/intent replay returns the original owner; another owner or source lineage is a conflict. Claims never expire or release on ambiguous submission. Verified-proof and durable operation admission are service prerequisites; this store checks structural body/tuple/digest identity but grants no cryptographic trust.

Replacement-quote withdrawal locks the exact current predecessor without changing
its version or retained state, then advances the parent admission version. The
transaction compares the predecessor request/history/generation/prepared state and
its permanent seal, preventing a stale withdrawal from succeeding over continuation
or replacement. Withdrawing a quote does not cancel the current child or authorize
any transaction. The service first authenticates the exact saved quote and its
server-bound parent version; replay after a lost response can prove old admission
impossible without repeating a mutation.
## Swap stage continuation

Swap predecessor stages become immutable once authoritative terminal evidence is
recorded. Successor admission checks the predecessor and atomically CASes
the current operation pointer with the new typed stage create. A terminal stage
never implies a completed operation. Unknown fee/rent cannot release a reservation:
only complete canonical terminal accounting enters remaining-budget calculations.
Confirmed unknown rent remains null in the original observation and retains its
full reservation in a separate unresolved aggregate/count. Exact immutable ordered
history proof can append resolved rent once, reducing that reservation and adding
the proven actual in the same operation/stage transaction. Duplicate proof cannot
release a reservation twice; contradictory proof is refused.
Execution and cleanup gas caps remain separate; accumulated rent is conservatively
retained even after cleanup. Canonical cost aggregates advance exactly once in
the same terminal-observation transaction. The predecessor audit is paged at 25
rows per call, with no lifetime retry limit. Same-stage generation advances only after finalized
no-effect evidence. Cleanup cannot return to an execution purpose. Current signer
and action authority is checked again before any new stage preparation or send.

# Workflow signing evidence

Fungible finalized accounting atomically creates unique issuance authority, optional reserve registration and an immutable projection receipt. A conditional typed Holon update is captured through `SurrealGeneratedStatementScope.Assign`; its result selects completed versus attachment-pending state within that transaction. Missing original ownership does not roll back finalized accounting. Later attachment is a separate atomic local-consent/owner/binding/version fence plus unique receipt; exact receipt replay survives subsequent owner or grant changes. The raw control statements express these multi-record invariants, while each ordinary mutation remains typed and parameter-scoped.

Run admission validates the captured credential under the same transaction as source access/quota and the run/outbox writes. Economic node admission locks current original credential/grants while consuming the scheduler epoch and persists the exact witness. These are atomic multi-record invariants; their raw statements remain flat and typed mutations use isolated generated parameter scopes.

`SurrealActionSigningAuthorizationStore` handles explicit original-participant consent for frozen legacy nodes with missing evidence. It locks current credential/grants and the exact active run, checks node version/input and the original encoded saga payload/gate, then commits the witness, immutable consent receipt and wake together. Ordinary outcome updates cannot attach new signing witnesses or new no-effect rejection markers. Historical run witnesses and an existing node witness are never replaced through this recovery path.

Ancestry inspection uses deployment_recovery_audit and immutable visited entries.
A page inspects at most 25 predecessors, verifies exact sealed owner/link/journal
identity, and commits row guards, visited entries and cursor version together.
The deterministic audit identity binds original parent and current head; immutable
head intent/predecessor fields are checked on claim replay. A repeated node or
broken link persists Invalid; there is no lifetime count cutoff. A completed audit
is usable only for its exact current head and its digest must be included in the
subsequent quoted predecessor witness. The API/admission wiring owns that gate.

## Webhook registration persistence

SurrealWebhookRegistrationStore uses the public generated POCO and typed query,
create and conditional update builders. Writes encrypt the supplied transient
secret with tenant/registration/revision binding; reads never decrypt it. A URL
change advances destination revision, secret replacement advances secret revision,
and all updates compare registration identity and version. Concurrent creation
returns a conflict instead of replacing the unique tenant registration. Unexpected
store errors and cancellation propagate. Authorized audited management mutations
and lease authorization must join this version protocol before public management
routes are enabled; this internal store is not an authorization boundary.

## Audited webhook management transaction

MutateAsync binds a caller-independent tenant target to its own direct credential
or the reserved current node operator. Child/delegated credentials cannot manage
the tenant's receiver. The shared credential/scope fence, active tenant record,
registration identity/version mutation and unique receipt commit atomically.
Operator first admission additionally checks five-minute authentication recency
using database time. Request identity includes actor, tenant and request ID; its
hash binds all command fields and normalized reason. Replays return the original
metadata receipt before destination/registration mutable checks, never a secret.

Only the winning first commit returns a generated secret. A lost response requires
an explicitly new rotation command; replay cannot disclose the retained ciphertext.
The generated secret/ciphertext and mutation are stable within transient database
retries. No audit field contains plaintext or ciphertext. Registration changes
cannot recall requests already authorized by the future delivery attempt lease.

Legacy registration replacement is an explicit management command. GET exposes a
keyed HMAC fingerprint of the complete stored snapshot, never the old secret.
The transaction rechecks the pinned legacy fields, creates fresh encrypted secret
material, clears the plaintext column, and appends the normal immutable receipt.
Missing historical revision fields are normalized only for this explicit path.
The old internal Upsert refuses legacy rows. A disable command may preserve an
unchanged now-blocked URL, because it authorizes no delivery; creation, enabling
or URL replacement still requires the destination policy.

Management withdrawal creates the same unique actor/tenant/request audit key with
a Withdrawn disposition. Its current-authority transaction and the original
registration mutation cannot both commit that key. Applied receipts are returned
as already applied, never inverted; withdrawn receipts permanently refuse later
mutation. No registration is created solely to withdraw an invalid first command.
The original full command hash is checked, and response-loss replay is stable.

Platform genesis definition creation uses a scoped typed create inside current operator authorization and unique-network claim transaction. Only pending diagnostics use the generic typed update. `HasVerifiedIssuanceAsync` traverses the exact completed operation, immutable projection/authority and reserve identity; bare legacy asset IDs cannot become verified platform issuance.

## Swap custody and completion epochs

External browser disclosure has a distinct potential-egress epoch.
`ExposeStageAsync` applies the current signing/action fence and atomically
persists consent hash, exact derived transaction identities and exposure time
before executable wire can leave the server. It never sets server first-send
authorization. An exposed stage cannot use unsent cancellation or return to
custody preparation. Exact replay checks the original participant and exposure
identity before consulting mutable authority. The public route remains gated on
complete provider observation and absence-proof recovery; missing signatures
must never be manufactured as a saved candidate.

Swap custody admission composes the shared live signing/action fence with typed
operation and planned-stage CAS in the existing paired transaction. Its new
epochs authorize only obtaining a signature; first-send remains a separate
transition. Cancellation can invalidate a delayed signing result before save.
Economic completion locks the current confirmed stage and writes one immutable
receipt only when gross input, accepted minimum output and platform fee agree.
Unknown prerequisite rent stays separately reserved after economic completion.
No public caller supplies terminal or accounting evidence.

Rent-release checkpoints and final proof lock the original allocation stage,
confirmed cleanup stage and operation aggregate in one transaction. The original
allocation is the unique release identity, including an externally closed prior
incarnation. Typed CAS composes these three records; raw LET/IF statements only
enforce all-or-nothing mutation. Historical allocated totals are preserved and
the separate released total can never exceed them. No unknown reservation is
released by an observed missing current holding.

## WTT owner takeover of a workflow operation

`TakeOverAsync` on an action-bound operation prepends `WttSigningAuthorityFence.ActionStopped` to the takeover
transaction (`// raw:` the run/execution liveness read and the version CAS on `wtt_operation` must share one commit). It
reads `action_run.status` and the node's `Running` execution on the frozen-input hash and throws the journal conflict while
both are live; the version CAS serializes it against any concurrent workflow stage claim. After takeover `Action(operation)`
returns null, so every later stage fence demands the owner's direct authority (RV-L5-07).

## WTT preparation identity

wtt_preparation claims one unsigned blockhash/round/account plan per physical protocol, operation, phase, stage and generation. Quote and owner are immutable binding checks rather than partition keys: changing either cannot create a second candidate. Signed bytes fill once under exact version, plan and unsealed state; retries recover the winner. Typed single-record CRUD expresses these invariants. This row alone never authorizes submission; the operation journal must atomically fence first-send and cancellation against the same preparation.

## Emit acceptance transaction

`SurrealActionEmitStore` commits the immutable deterministic receipt and its optional tenant outbox event in one multi-record transaction. The action execution fence locks the frozen input/run, the shared signing fence checks the saved original credential and currently selected action-execute consent, and a typed registration CAS serializes destination selection with management. Raw statements only compose those atomic invariants. Equal committed receipts replay before mutable authority/destination checks. Invalid config commits a rejection with no event; missing registration remains unaccepted. Ordinary reads, creates and registration CAS use typed APIs. Recovery and delivery wiring are a separate required implementation step; this store alone does not replace the existing worker.

## Webhook delivery authorization leases

`SurrealActionWebhookDeliveryStore` composes typed mutations in an atomic registration/event transaction. Each attempt pins the current encrypted secret revision while locking the exact active original registration and destination. It increments the event epoch and attempt count before egress. Database-time due/expiry predicates and exact holder/version completion prevent a stale worker from overwriting a replacement claim. An already-authorized request may finish after rotation or disable; every later attempt needs fresh authorization. Finishing or retiring an abandoned exhausted attempt clears retained ciphertext. Lost HTTP acknowledgments may redeliver stable body/idempotency bytes.

Unavailable original destinations or missing acceptance evidence are conservatively dead-lettered under the event epoch only after its lease expires. A concurrent restoration can therefore still require an explicit audited retry; no unavailable-state transition authorizes a send or changes a pinned destination. Error storage accepts bounded symbolic codes, not response bodies, URLs, secret values or exception text. Worker integration, audited retry and shared consent migration remain required.
# Consent lifecycle admission

`SurrealConsentLifecycleStore` commits the original explicit command receipt, new
grant, audit and frozen event in one transaction. It locks current authenticated
owner authority, active target tenant and the observed registration version. Missing
registration produces an explicit undeliverable event; a later registration never
retargets it. Unique command creation arbitrates overlapping requests, and replay
returns the immutable original receipt even after later revocation. The transaction
is the raw-query invariant; ordinary writes and reads use typed builders. This new
store is not yet wired to the legacy consent controllers: participation, revoke,
expiry, offboarding and sovereignty-claim migration remain required before cutover.

## Atomic local hold commands

SurrealHoldCommandStore composes typed writes for the current direct/workflow authority, exact reserve version/identity, hold liability and immutable command receipt in one transaction. Workflow writes also join ActionExecutionFence and the saved per-node signing witness hash. Raw statements only compose those cross-record fences. Chaining creates the child and closes its parent without drawing the reserve again; a refund atomically returns the one preserved commitment. Original receipt replay precedes mutable authority and never rewrites a historical outcome from the current aggregate. Concurrent commands compete on the aggregate epoch and unique command receipt, with prebuilt conflict retries.

This initial store slice is not registered or wired. Economic-source proofs, chain readiness/cost mandates, funded and outbound stages, participant acceptance and caller migration remain required. The old Hold writer must be retired at cutover; it cannot be a fallback when the atomic command path refuses.

The owner revocation path conditionally advances the exact grant version and original
scope/tenant/time snapshot alongside one immutable transition, audit and event. Its
deterministic transition ID makes concurrent revocations converge on the persisted
terminal time. Existing legacy revocation facts are returned with notification evidence
explicitly absent; they are not retrospectively presented as atomically recorded.
`ConsentNotificationPlan` shares destination capture between admission and revocation,
including a tenant-row lock that permits inactive tenants during revocation. Registration
management uses the same tenant row, so destination creation and capture serialize.

Direct user-authenticated participation admission shares the original command
transaction. It creates or advances a tenant/reference membership epoch and refuses
a closed boundary. Every fresh grant increments that epoch; an offboarding
transaction must compare the epoch captured before enumerating grants, preventing a
newly admitted grant from escaping the sweep. Original receipt replay bypasses this
fresh-admission check because it does not create authority. Internal provisioning
still requires its own exact source witness and stable command migration.

Participation also requires active tenant-approved enrollment evidence for the exact
subject and membership boundary. An owner login alone cannot create governance
membership. `AdmitProvisionedAsync` is the approval-producing path: it uses the
actual tenant's `TenantProvision` witness, fixed implied scopes and deterministic
tenant/child/external-user command. The same transaction locks the active child at
its original tenant owner, external ID and authentication watermark. A claim racing
admission invalidates that source witness. Original receipt replay after a claim
does not create a new grant. Direct participation can only reuse approved enrollment.

`OffboardAsync` reads the membership epoch before enumerating unrevoked participation
grants, then closes it and revokes that exact set with audits/events/proofs in one
transaction. A fresh admission advances the epoch and forces the sweep to retry;
concurrent revocation similarly prevents partial closure. An empty membership still
gets a closed tombstone. The stored closure receipt preserves the original count and
time on replay. One destination snapshot is shared across the batch, preventing a
mid-batch registration change from producing inconsistent receivers or parameter
bindings. Legacy unrestricted mutation callers must be removed before cutover.

### Consent delivery evidence and leases

ConsentNotificationPlan emits immutable transition evidence for every grant, revoke, expiry and offboard event in the same lifecycle transaction. Evidence retains the original receipt, exact canonical body hash and original registration/destination revision. Delivery validates that evidence without consulting mutable current grant scopes or retargeting to a new registration. Legacy rows without evidence cannot send. SurrealConsentWebhookDeliveryStore uses typed event/registration mutations inside an atomic claim: current registration and secret revision, database-time due/lease checks, exact event epoch and consumed attempt counters. Completion requires the unexpired holder; storage failures remain outside HTTP result classification. Unavailability suspends only an unleased exact epoch. Management retry and old caller/worker cutover remain required before registration.

### Hold rail preparation (B2 authoring)

SurrealHoldCommandStore preparation transitions join current exact credential/grant/wallet authority, the original action fence, active liability slot and command version. Canonical prepared wire is persisted before first-send authorization. The release cutoff is checked against database time in that first-send transaction. Recipient wallet ownership is rechecked at the same boundary; no caller-selected recipient can replace the immutable plan. Submitted recovery will observe or rebroadcast only the saved bytes; completion/projection and caller cutover are still under implementation. Raw statements compose these cross-record atomic invariants; ordinary mutations use scoped typed writers.

Hold rail finalization joins the exact Submitted command, immutable wire hash, active original liability, reserve version/counters and terminal receipt. It intentionally performs no fresh signing authorization: final accounting records an already finalized effect. A conflict preserves Submitted for authoritative reconciliation; it never sends replacement wire or independently draws reserve counters.

### Account-claim consent transaction

SurrealUserStore's claimOwnership authentication CAS now joins all grant revocations, audits, immutable lifecycle proofs and frozen notifications in one transaction. SurrealConsentLifecycleStore.ClaimRevocationsAsync prepares the exact original unrevoked grant set; the transaction checks its cardinality and every original grant/version before changing them. Fresh admissions lock the same user authority record, while the ownership/watermark CAS prevents post-claim provisioning grants. Per-tenant notification witnesses have distinct bound-parameter scopes, so multi-tenant claims retain their own destinations. A failed proof, destination fence or grant epoch aborts the user mutation too. Signing authority remains the timestamp/ownership/grant facts; notification delivery never decides it.

Acceptance stage transactions bind the original command and actual recipient wallet/grant authority before custody and first send. Observation commits exact saved-wire evidence without requiring that old signing authority remain live. No-effect transfer verdict uses a typed single-record CAS and retains wire/history; it does not release the active liability or authorize a replacement.

### Tenant wallet bootstrap authority

BootstrapTenantAsync serializes current tenant credential scope, exact child ownership/authentication cutoff, open provisioning membership, live wallet-management/KYC-read grant and deterministic wallet create/read. Both an existing wallet and a newly created one require those current witnesses. LocalEntityMutationFence.Wallets joins other wallet mutations. A new bootstrap wallet becomes default only when no default exists; tenant bootstrap preserves an existing default and does not run a later unfenced default rewrite. A concurrent claim/offboard/revocation aborts the transaction.

### Atomic claim invitation

TryClaimAuthenticationAsync replaces the boolean claim switch. Exact invitation record ID, token, child, tenant, expiry and unconsumed state join the active user credential/ownership CAS and every consent revocation in one transaction. Database time decides invitation expiry. A user or grant conflict rolls back invite consumption; the standalone token consume API is retired. The normal wallet-link method cannot transfer ownership.

### Current tenant custody access

TenantCustodialAuthorityFence captures actual tenant credential scope bounds, exact child ownership/authentication watermark, open membership epoch and the original deterministic provisioning grant. Additional direct Participation grants do not replace that source or cause ambiguous selection. Its flat guard statements join a consuming transaction; the response validator runs them after status collection, so account claim, offboarding, expiry and credential revocation cannot turn a historical identity correlation into fresh wallet/KYC access. KYC effect admissions must also join the same captured fence; a controller precheck alone is not mutation authorization.

### Consent command withdrawal

Consent command admission and withdrawal both CREATE the same actor/request-keyed command row in their owner-authorized transaction. Withdrawal stores the canonical intent hash and a permanent Withdrawn marker; it does not require the target tenant or expiry still be valid. An admission winner returns its immutable original receipt and requires ordinary grant revocation to remove existing authority. A withdrawal winner prevents every delayed admission of that command. Changed intent never reuses either outcome. Fresh admissions accept the existing IsIssuableByUser vocabulary; original successful receipt replay precedes that policy check. No withdrawal event is emitted because no grant transition occurred.

Custodial KYC admission uses IKycCustodialAdmissionStore for attempt creation,
retirement and document attachment. Its captured child/enrollment/credential
fence is part of each mutation transaction. Hosted first egress joins the same
fence with HostedKycCreationFence and the creation ownership CAS. Prepared
provider attempts cannot establish first-egress authority: their recovery claim
requires an already persisted CreationStartedAt. Once admitted, exact-request
provider replay and callback accounting preserve the original authorization;
they do not manufacture a new tenant session. A refused send can leave inert
prepared/claim records for existing audited reconciliation, never provider egress.

## NFT protocol cost offer journal

SurrealNftProtocolCostQuoteStore uses typed keyed CREATE/SELECT and never updates an offer. A duplicate ID returns only an identical quote belonging to the exact actor and tenant; a changed expiry, economics or draft is a conflict. Reads retain expired receipts for admitted recovery and verify the persisted payload/hash and ownership indexes. This journal does not authorize signing: lifecycle admission must atomically fence the selected receipt, database expiry and actual current authority, then preserve the accepted quote in the command.

## Hold release mandates (B2 source under review)

An immutable server offer precedes explicit acceptance. Acceptance locks the original
owner credential, liability receipt, recipient wallet and unique active mandate
pointer. The mandate separately bounds native network fees, locked balance and
nonrefundable costs with exact payer/refund identities. Missing category limits are
zero. A deterministic mandate operation uses the existing command preparation,
saved-wire first-send and finalized accounting path.

HoldReleaseMandateFence alone composes the shared internal credential/grant primitives
without the original JWT expiry check: the same transaction first locks the exact
persisted active mandate, canonical terms, current authorization revision, database
cutoff/expiry and reserved liability. Current user/key/grant revocation still applies.
No free-standing request witness can select that mode. Reauthorization appends an
immutable receipt and cannot revive revoked or terminal mandates. Replacement
generations remain explicit and are not authorized by a release mandate. Confirmed
accounting makes its mandate terminal without requesting fresh signing authority.

Every mandate-backed preparation/save/first-send CAS also creates an immutable
HoldMandateUseReceipt with the exact mandate version, terms hash, current authority,
authorization receipt and prepared-wire hash. The captured revision is rechecked
inside the transaction. Later renewal cannot rewrite which authority admitted an
earlier stage; rollback leaves neither a stage change nor a use receipt.

Mandate management reconciliation first checks the exact immutable request receipt,
then reads the current version, then repeats the receipt lookup before declaring an
older request obsolete. Only a strictly higher durable version fences its old CAS;
an equal version or unavailable read remains unresolved. This read path neither
withdraws a mandate nor infers settlement, and a generic conflict is not proof that
a client may discard its saved request.

Workflow-backed mandates use a separate HoldReleaseWorkflowFence, never a relaxed
ActionExecutionFence. It locks the exact originating execution record in Succeeded,
its original run in Running/AwaitingReconciliation/Succeeded, saved frozen input and
signing witness, original immutable opening receipt, and matching Hold opening
pointer. Current original credential/grants remain required. The source field is
immutable and included in mandate-use receipts. Creation from a workflow must be
part of the accepted opening transaction; that adapter/consent integration remains
under authoring, so this helper alone does not activate workflow release mandates.

### NFT selected cost admission

Fresh quoted Mint/Create generation zero binds the immutable command, actor/recipient, exact payer wallet/address, tenant, chain/network and draft hash. NftProtocolCostAdmissionFence composes a typed keyed receipt read and exact payload/index/expiry assertions with lifecycle CREATE and any workflow authority guards. Expiry uses database time. An incumbent lifecycle is returned before fresh quote validation, including cancellation tombstones; callers still compare the original request before advancing. ClaimAsync returns null for rejected fresh cost admission. The cost receipt is immutable and lifecycle-bound, so it needs no mutable consumed flag. Null cost fields omit serialization to preserve old command and signed intent hashes.

### WTT historical evidence progress

SurrealWttEvidenceProgressStore is a readonly-proof journal, not admission or signing authority. Canonical provider binding includes the immutable physical network, operation/preparation and fixed historical query boundary. Typed CREATE establishes one initial state; exact version/state/cursor/digest CAS advances one bounded page. Repeated cursor digests are refused across all prior pages, and only exhausted pagination marks completion. The provider owns validation and compact history state; unknown or incomplete chain evidence cannot become settlement merely by advancing a cursor. Unit and real DB conflict/lost-response/completion fixtures are authored but unrun.

### NFT workflow explicit cost acceptance

SurrealNftWorkflowCostAcceptanceStore creates the immutable acceptance audit and NFT lifecycle in one transaction. Original workflow credential/grants, current accepting consent, unchanged run/node authority, database-time cost expiry, and a keyed exact execution/version/signing/reference lock participate in that transaction. Advancing the execution version serializes competing acceptances and gives stale-request recovery a monotonic boundary. The frozen draft is read and matched before the transaction, then its exact digest is fenced at commit. Original acceptance lookup precedes mutable authority and price checks. Raw statements express only this cross-record atomic invariant. No standalone audit creation API is exposed.

### Unaccepted workflow Hold recovery

WorkflowOpeningRecovery changes only an AwaitingConsent/Opening command and its
immutable audit under one version CAS. Replacement preserves the original frozen
intent, execution identity and mandate terms; only fresh provider quotation is
rotated. Original source and actual accepting consent must remain current.
Withdrawal requires the original actor's current full login, but no live delegated
grant or workflow because no liability exists. It cannot withdraw an accepted
opening. Both transactions compete against the atomic opening/reserve/mandate commit.
The original disclosure and new result remain in immutable audit data. Exact request
replay returns its saved result. Child-hold opening uses the same transaction and
binds its mandate to the newly created child rather than the settled parent.

### Original authority after NFT cost acceptance

The immutable lifecycle command now retains its exact WorkflowCostAcceptance receipt (null omitted for historical serialization). Acceptance-store creation checks that link against the audit it commits. NftWorkflowCostAuthorityFence participates in existing requireActionAuthority preparation/first-send updates: it rechecks original credential/grants, exact acceptance audit JSON/hash, exact original execution ID/signing/reference/frozen hash, and the unchanged shared run/node fence. It does not refresh the old cost expiry after admission. Signature-free observation/accounting updates remain independent. Authored real-store fixtures revoke original authority after admission and assert new-effect updates refuse while observation updates remain possible. No gates run; independent delta review pending.

NFT cost retirement atomically checks absence of the exact acceptance audit, advances the unchanged scoped execution version and creates an immutable retirement receipt under current NftMint consent. It never alters frozen input or original signing authority. Acceptance contends on that same version, so a delayed old request cannot admit after retirement. Receipt replay survives later authority changes; recovery uses the existing ordered execution/audit query to report Applied or Obsolete. This is a genuinely multi-record atomic transaction; ordinary reads/mutations use typed builders. See the active platform-closeout retirement design and race fixtures.

Direct NFT mint commands retain the captured actor credential, selected grants and owned wallet as ActorAuthority. Admission and new preparation/first-egress transactions check that original authority with the shared signing fence. Missing historical authority cannot authorize new direct mint signatures; observation-only updates remain allowed. Public JSON cannot supply this authority: controllers capture it and manager adapters forward server-only AuthenticationWitness. This first slice is mint-only; the active participant-authority design tracks remaining operations and stage consent.

Pre-admission cancellation has no signing wallet. Only the exact empty terminal cancellation marker recognized by NftCancellationTombstone bypasses original-signing authority on fresh claim. It contains no transaction, asset identity, history, fee or signing authority and cannot transition into an economic intent. It permanently occupies the original request key; a delayed admission cannot replace it. Caller actor/tenant are supplied by the authenticated cancellation route. Arbitrary Cancelled rows do not qualify.

### Exchange source commitment

SurrealHoldCommandStore.Exchange journals a deterministic tenant/API-key/request
source under the actual tenant credential and separately selected participant
grant, watermark and wallet. Its dedicated opening path reconstructs only the
saved intent and consumes the exact source version inside the existing reserve,
liability and immutable Hold receipt transaction. Generic local commands cannot
supply economic-source authority. Newly committed purchase liabilities use
AwaitingSourceSettlement, so an unresolved exchange cannot be released, chained
or refunded through ordinary Held transitions. Final chain evidence, source
compensation and automatic-mandate integration remain in the accepted cutover
work; this initial source slice is not registered or a replacement for callers.
# Ethereum DEX allowance ownership

`SurrealEthereumDexAllowanceStore` serializes each physical network, owner, token,
and executor allowance across operations. Its raw transaction locks the owning
swap operation together with the resource; lock expiry never releases ownership.
A prior canceled operation can be displaced only when it is still `Cancelled` and
no historical stage was authorized or externally exposed. The displacing
transaction touches that operation row. Cancelled is not permanently egress-free:
cleanup legitimately moves Cancelled -> CleanupReady -> revoke. The invariant is
that every transition out of Cancelled and every egress write (authorize, expose)
updates the same operation row through `CommitStageAsync`. A concurrent cleanup
start or exposure therefore conflicts with the displacement instead of crossing it.
Release uses an exact saved confirmed Swap/RevokeAllowance stage and immutable
observation under a stage/version fence. A pending, failed or approval receipt
cannot release ownership. The provider remains responsible for authoritative
chain proof before the domain records that receipt. A contending claim first
retries that same release from the holder's saved current stage, so a lost
eager release cannot strand the resource; it never releases on absence.
The release mutation result is captured as `$allowance_released` and returned by
name (no positional result offset). If the conditional update matches no row, or
the transaction conflicts, the store re-reads the resource. It reports success
only when the winner released this same operation with this same stage. A
contending claimer and the eager releaser therefore agree instead of one
reporting a spurious failure.

A confirmed stage normally proves its winner through its saved candidate
(`PreparedJson`). An operator-settled stage proves it through its recorded
settlement (an `OperatorSettlement` evidence member). The swap journal admits
that member only through `SettleOperatorReviewAsync`, so `ReleaseAsync`
accepts either. The zero-allowance `AllowanceRelease` binding is required in
both cases.

### Swap operator settlement

`SurrealSwapOperationStore.SettleOperatorReviewAsync` is the only writer of an
operator-settled observation. `RecordObservationAsync` refuses one.

- It requires the operation and current stage in `NeedsOperator`, at their
  expected versions, with egress.
- The audit's `ReplacedObservationHash` must equal the hash of the stage's
  current review observation.
- `SwapOperatorSettlementContract.IsValid` must pass.
- One `CommitStageAsync` writes the terminal stage and moves the operation to
  `StageResolved`, charging the actual fee to the effect or cleanup total.
- An exact replay of a terminal stage is idempotent.

`ProvenConfirmedStage` lets `CompleteAsync` and `CompleteCleanupAsync` accept
either proof. `ValidJournalObservation` accepts a recorded settlement on a
terminal stage, for continuation and cleanup admission.


### NFT participant stage consent

StageConsentsJson is append-only under the lifecycle version CAS. A fresh entry binds the actual participant credential and owned wallet to one lifecycle, stage, generation, canonical full intent and physical protocol identity. The stored acceptance request also binds the reviewed prior version/state/stage/generation and a stable request ID. Only an eligible awaiting stage or a replacement after recorded authoritative no-effect may append a new stage/generation; Prepared/Submitted rows cannot reinterpret a new acceptance. The one exception is a participant renewal (`CanRenew`): while Preparing or Prepared, the same participant, tenant and wallet may append a fresher credential for the unchanged stage, generation and intent hash, and `Current` then returns that latest entry. The batch first proves the superseded credential is no longer live (JWT expiry, user deactivation or watermark advance, API-key revocation or expiry), so a live participant cannot keep advancing the version to invalidate the requester's in-flight requests. A tenant-grant-only revocation becomes renewable once the held credential expires. The atomic mutation checks the effective requester authority (the original witness or its latest renewal) and the separate participant authority together. Preparation and first-send repeat both checks. Observation preserves the history without requiring current signing authority. A saved request replay may resume only its same stage and generation; a later stage remains observation-only.
# Exchange source and finalized accounting

Exchange custody preparation and first egress join the exact source CAS with both
original authority fences. Prepared wire persistence and final evidence recording
do not invent renewed permission. Confirmed source accounting atomically credits
the original treasury, activates only its original AwaitingSourceSettlement hold,
and creates a permanent settlement receipt. It preserves a closed treasury's
restriction while accounting for actual received funds. Receipt replay does not
depend on bounded movement-key windows or current signing permission. Required
no-effect compensation, mandates and legacy caller cutover remain tracked work.

The exact ExpiredAbsent source can now atomically cancel its awaiting-source hold,
return only that original reserve partition commitment, and write the same unique
settlement receipt identity. It never credits or debits treasury funds. Unknown,
partially indexed, or mismatched evidence cannot enter that transition. A completed
receipt survives later permissions and reserve movement-key eviction.

### Replacement NFT cost receipts

ReplacementCostConsentsJson preserves every accepted generation while RequestJson keeps the initial command and offer. Appending requires the unchanged original lifecycle version and exact prior authoritative no-effect evidence. NftReplacementCostContract matches the full new-generation intent, physical network, original payer and current accepting authority. The acceptance transaction joins the effective requester authority, the accepting mint authority and database-time immutable receipt expiry. After that commit the receipt is immutable evidence: preparation and first send recheck its binding (`NftReplacementCostContract.Matches`) and the current requester authority, not the accepting credential or the offer expiry. The branch applies only to unconfirmed replacement Create generations.

ReplacementCostRetirementsJson supplies a negative receipt for an unanswered exact acceptance. Its version advance competes with acceptance, so only one can commit. Retirement requires the current original payer's mint authority but does not revive original signing authority or require the expired quote. All other lifecycle fields and existing histories remain unchanged. Reconciliation reads one lifecycle snapshot, returns Applied for exact accepted audit first, then Obsolete for retirement/higher version, otherwise Unresolved.

ActorContinuationsJson is append-only under the same version CAS and requires the action-authority path. An appended renewal must review the exact current version/state/stage/generation, reuse no request ID, change no other lifecycle field and bind the original direct requester, tenant, wallet and operation scope. The requester fence for every later new effect uses the latest matching renewal instead of the lapsed original witness; fresh admission requires an empty renewal history and the empty cancellation marker rejects one.

### NFT effect authorities

`NftEffectAuthorities.Select` is the single list of credential witnesses each lifecycle write fences, shared by the durable store and the in-memory test store. `NftLifecycleWrite` names the kind. A fresh admission fences the original requester (the empty cancellation marker and the deterministic workflow rejection fence none). An effect (participant consent append, preparation, first send) fences the effective requester plus the current stage participant when the stage needs one. A replacement-cost acceptance additionally fences the accepting credential and the receipt's database-time expiry. A requester renewal or participant renewal fences only its new credential, because it signs nothing; the next effect checks everything again. Workflow fences (`ActionExecutionFence`, workflow cost authority) join admissions, effects and cost acceptances, never renewals. A missing or non-binding witness fails closed before the batch is sent. Every contended lifecycle batch runs through `SurrealTransientConflict.RetryOnConflictAsync`; a retried loser fails its own version predicate (null or `NFT_LIFECYCLE_CHANGED`) and the caller re-reads. The mutation result is captured as `$_nft_effect_row` and returned by name, not by a guard-count offset. `CancelUnsentAsync` fences the requester's current consent (`SigningAuthorityFence.ForConsent`, operation scope) with the cancellation.

A recipient-stage cost acceptance (`StageCostAcceptance`, F-M11) fences only the accepting payer's credential plus the stage receipt's immutability and database-time expiry (`NftProtocolCostAdmissionFence`) in the same batch as the version CAS; it signs nothing, so no workflow fence joins it. Every other authority-bearing write on a cost-bearing Solana stage (preparation, first send) must pass `NftStageCostContract.AdmitsWrite` before the batch is built; the consent history is part of the row, so the version CAS proves the checked history is the committed one. A workflow-authority write (`workflowAuthority: true`) is refused once `NftActorContinuationIdentity.TakenOver` holds on the stored row, and every lifecycle UPDATE also predicates on the unchanged `actor_continuations_json`, so a takeover committed between the read and the write makes the stale run's write match no row (RV-L5-09). `FindCustodialWalletIdsAsync` reads Platform wallet ids for one exact (chain, address) through `wallet_chain_address` (Ethereum also probes its checksummed and lowercase forms); a wallet whose stored chain spelling differs is simply not found, which fails closed.

## Hold command and exchange source transactions

`SurrealHoldCommandStore` partials compose multi-table atomic transactions. Every raw statement is
`BEGIN`/`COMMIT`, an `IF ... THROW` guard over a captured typed mutation, or
`SurrealGeneratedStatementScope.Assign/Apply/RequireResult` around typed builders. Each composition
carries one `// raw:` pointer naming its invariant. A captured conditional mutation that matched
no row aborts the whole batch, and the caller re-reads. A loser of a race replays the winner's
immutable receipt rather than failing.

- **In-batch receipt reads** use `AssignRecord` (`LET $v = (SELECT …)[0]`, the record or `NONE`), never
  `Assign`. A `Key()` select is an array, and the field comparisons and `crypto::sha256` checks
  would always fail closed. Each read carries a `// lock:` note. Receipts are create-only, and the
  liability, source or run CAS in the same batch serializes the operation.
- **Contention is re-planned, not refused** (`Contention` partial). A counter-row CAS that loses to
  an unrelated writer (another hold drawing the same reserve row) throws `HOLD_COMMAND_CHANGED`.
  The retry wrapper would only replay the stale version. So local commits, units commitments,
  terminal settlement and purchase recovery re-read and re-plan up to `MaxReplans` (4). Each pass
  first re-reads the receipt, and a loser returns the winner's receipt.
- **Terminal accounting** (`TerminalAsync`): every terminal op (settle, compensate, abandon) plans
  from a fresh source read. Before any `null`, it re-reads the one settlement receipt and returns it
  only when the source is in that op's own terminal state. An absent group can never answer as
  settled.

- **Exchange units** (`ExchangeUnits`, `ExchangeSettlement`, `ExchangeAbsence`): commitment,
  finalized settlement, proven-absence compensation and unsent abandonment each join, in one
  batch, the source CAS (`SourceTransition`), the reserve ledger version CAS, the treasury
  ledger CAS where it applies, and the immutable `ExchangeHoldSettlementReceipt`. A held buy also
  joins its liability and, when one was accepted, its release mandate. A sale's partition
  `Return` is clamped to the outstanding issued units (`ReturnableUnits`). Treasury chain
  identity is compared case-insensitively, as the treasury ledger stores it.
- **Authority fences** (`ExchangeAuthorityFence`): tenant API key, participant watermark, wallet
  and exact grant are re-locked at commitment, preparation and first egress. Each prefix is a
  caller-fixed lowercase token (`exchange_source`, `exchange_units`, `exchange_wire`,
  `exchange_opening`, `hold_exchange_original`). Captured variables are `$_<prefix>_participant`,
  `_wallet` and `_grant`. The tenant fence delegates to `SigningAuthorityFence.ForConsent` with
  `<prefix>_tenant`.
- **Purchase recovery** (`ExchangeRecovery`). The request carries the reversal's **cumulative**
  want from the purchase and the lot's whole attributed share (`ClaimSourceUnits`). Each step:
  - recovers only `want − already recovered by this reversal`, further capped by
    `share − allocation.consumed` over every reversal of the lot;
  - writes an immutable receipt keyed on (reversal, lot, source, recovered-before), with
    `OperationId` = the reversal id, so `ListReversalRecoveriesAsync` finds them by index;
  - lets concurrent passes collide on the receipt position: one commits and the other replays it;
  - makes the allocation CAS (or deterministic create) hold the cap at commit;
  - rounds hold units **up**, capped at what is still held. Credit is
    `min(delta, ceil(share × spent ÷ purchased))`.

  **Clawback reservations** (`ExchangeClawback`) use the same want, cap and receipt-position rules,
  with kind `FiatReversalClawback`. They apply to a direct buy, or a held purchase whose liability
  is `Released`. The reserved units are `min(ceil(received × delta ÷ spent), delivered − clawed)`,
  where `clawed` sums `ClawedAssetUnits` over every allocation of the purchase. A version CAS on the
  completed source row serializes all clawbacks of one purchase, so the delivered cap holds at
  commit. The receipt is committed **before** anything is sent, and its id is the ladder's claim
  key for that one move.

  A partial step marks the parent `PartiallyRecovered`, creates the remainder child with
  `InheritsReserveCommitment` plus a recovery `HoldCommandReceipt` proving it, and narrows any
  active mandate (`HoldReleaseLineage`, quote id recomputed). `GetPurchaseLiabilityAsync` follows
  the remainders. Once the liability is `Released`, it names the recipient wallet from the
  completed `Release` command's rail plan, so the reversal raises a clawback instead.
- **Release of a purchased liability** (`HoldReleaseExchangeFence`): only a `Completed` source
  whose settlement receipt, opening receipt hash and liability pointer all still match may
  authorize a release. A remainder mandate additionally needs its exact lineage receipt hash.
- **Local commands** close an Active mandate atomically, and the transition `WHERE` pins both
  `EconomicSourceId == null` and the mandate pointer that was read (see `Services/Hold/AGENTS.md`
  §purchased-liabilities and §mandates).

## consent-grant-store

`SurrealConsentGrantStore` is read-only (round 4). Every `consent_grant` write goes through
`SurrealConsentLifecycleStore` (command-id single winner, version CAS, audit + webhook
outbox in one transaction). The former `UpsertAsync` (fresh `Guid.NewGuid()` outside the
lifecycle transaction), `ListByTenantAndParticipationRefAsync` and `RevokeAllByGrantorAsync`
(a raw multi-row UPDATE with no version bump, audit or notification) had no production
caller and were deleted. Reads are typed (`SurrealQuery<ConsentGrant>`); store failures
propagate to the HTTP boundary instead of being swallowed into an error result.

## wallet-ownership-proof

`SurrealWalletOwnershipProofStore` keeps one row per canonical `(chain, address)`
(`WalletAuthIdentity.TryCreate`), record id = sha256(`chain:address`) — the deterministic id
is the lock (transaction-scope rule 3), so concurrent proofs for one address serialize on
the key and the last verified proof wins. The row is written only after
`WalletAuthManager.VerifyAddressControlAsync` atomically consumed a live challenge and the
signature verified; `wallet` rows are never touched, so a full-POCO wallet upsert cannot
forge or clobber a proof. `GetAsync` re-checks the stored chain/address against the key.

## RETURN slots and explicit NULL (round 4 SQL-01/02/12)

Probed on SurrealDB 3.1.4 (HTTP `/sql` and `/rpc`): `BEGIN; LET; IF; RETURN $x; COMMIT;`
returns five slots — `COMMIT` keeps its slot, statements *between* a top-level `RETURN` and
`COMMIT` are skipped and dropped. End-relative reads (`Statements.Count - 2`) therefore
happened to be right only while `RETURN` stays directly before `COMMIT`; the claim
(`SurrealUserStore`) and tenant-bootstrap (`SurrealWalletStore.TenantBootstrap`) batches now
capture `statements.Count` before adding `RETURN`, like the WTT stores. Raw `SET col = $x`
with a possibly-null C# value: a SCHEMAFULL `option<T>` column rejects `NULL`
("Expected `none | string` but found `NULL`"), so `SurrealSagaStore` writes
`$x ?? NONE` (probed: NULL → field absent, a value → stored).

## action-dependencies

`SurrealActionStore` persists `ActionDefinition.Dependencies` in `action_dependency` (round 4):
cleared and re-written with the nodes/edges on upsert, read back in the same combined child
SELECT, deleted with the head. Before this, dependencies round-tripped as empty, so run-start
dependency enforcement (`ActionManager.EnforceDependenciesAsync`) only bit in-memory. The store
is still a non-transactional multi-request fan-out (pre-existing shape; a partial write is
visible on failure) — moving the whole aggregate into one `BEGIN…COMMIT` is a follow-up.

- **Custodial NFT recipient lookup (RV-L4-08):** probe the canonical chain/address spellings from `Helpers/NftRecipientLookupForms` through the `wallet_chain_address` index; never case-fold Solana/Algorand addresses.

## Typed conditional mutations always carry a guard (SurrealForge 1.3.0, round 4 r4/fix-core2)

`SurrealWriter.UpdateOnly/DeleteOnly(...).Build()` throws without a `Where`. Never satisfy it with `Where(_ => true)` in
production: the guard restates the state the surrounding batch (or call) just read, locked or wrote, so it is always true on
the allow path and turns a concurrent change into "no row affected" instead of a blind write.

| Site | Guard | Why it holds on the allow path |
|---|---|---|
| `SurrealActionLocalMutationStore` holon/wallet delete | `user_id` = owner of the witnessed read row | the row witness locked and compared the full row earlier in the same transaction; plan validation requires owner == actor |
| `…Witnesses` `AppendHolonNulls/AppendWalletNulls` | `user_id` = owner of the row just upserted | same transaction, written two statements earlier |
| `SurrealActionSigningAuthorizationStore` run / saga step | run `status IN (Running, AwaitingReconciliation)`; step `status = Parked` | both verified (and the run version-bumped) earlier in the batch |
| `SurrealHolonStore.DeleteAsync` / `SurrealWalletStore.DeleteAsync` | `user_id` = owner this call just read | an owner change between read and delete affects 0 rows → refusal, not a delete of someone else's row |
| `SurrealFungibleCreationStore.AuthorizeAsync` pointer | `version` = `expected.Version` | the lock statement set version to itself and asserted it |
| `SurrealGovernanceStore.FinalizeAsync` | `definition_hash` matches and `final_result_json = NONE` | the `$_finalizing` lock asserted exactly that |
| `SurrealNftLifecycleStore.CommitAsync` release | `version` = the authority version just created/upserted | written earlier in the same batch |

`SurrealQlSourceRulesTests.Typed_conditional_mutations_always_carry_a_Where` also catches builders split across statements
(`var update = UpdateOnly…; …; update.Build()`), which is how the two `Append*Nulls` sites escaped the first ratchet.

`HolonPoco`'s `[References]` on `parent_holon_id`/`user_id`/`source_holon_id`/`origin_user_id` carry `Optional = true` to mirror
the canonical `Persistence/SurrealDb/Models/Holon` (`option<record<…>>`): SurrealForge reads optionality from `[References]`
before the column type, so without it `Unset(ParentHolonId)` threw "required field cannot be unset".

`SurrealQuery.Combine` refuses a single query: `SurrealFungibleCreationStore.UpdateAsync` sends its lone progress statement
directly when no effect authorization (and so no BEGIN/COMMIT fence) is needed — a single statement is already atomic.

## wallet-claim-query-binding

`SurrealWalletAuthClaimTokenStore.GetByTokenHashAsync` scopes its typed equality
query through `SurrealGeneratedStatementScope.Apply` with `claim_lookup`. The
`token` column holds the `h1:` digest, never the raw token; the store refuses to
create or look up anything else (Services/Auth/AGENTS.md §claim-invites).
SurrealForge 1.3 derives parameters from column names, so renaming a captured
local cannot avoid the protected `$token` variable. The shared helper renames
only generated parameter tokens and bindings; exact equality and LIMIT remain.
The real-DB fixture checks two distinct invites and a miss, plus unchanged invite
state after rejected claims and one-time consumption after a valid claim.

## hosted-identity-binding-parameters

`SurrealKycIdentityStore.BindVerifiedUserAsync` retains one atomic transaction
for evidence serialization, the exact hosted lease, active-attempt authority and
person membership. Its attempt parameters use `_attempt_*`; identity mutation
parameters use `_binding_*`, disjoint from the shared evidence and lease guards.
SurrealForge Client 1.3.0's `SurrealQuery.Combine` XML contract merges parameter
dictionaries with last-write-wins semantics. The attempt's provider key
(`identomat`) must therefore never share a parameter with the persisted provider
enum (`IDENTOMAT`). The tenant parameters are distinct even when their values
currently agree. All existing lease, expiry, subject, tenant, selection, trust and
membership predicates remain enforced.

Optional identity fields use `$parameter ?? NONE` in the atomic mutation to
translate nullable CBOR parameters into absent SCHEMAFULL option values. Required
identity/provenance fields retain their required types. The real-DB binding
fixture covers node-direct and tenant-scoped success with null personal fields,
and changed lease/provider/selection/trust authority with transaction rollback.
