---
type: doc
scope: Core/Surreal
---

# Core/Surreal — SurrealDB primitives

Storage-engine primitives that are domain-agnostic and reused across every
SurrealDB-backed store. SurrealDB is the sole storage engine.

## Â§scalar-string-binding

`SurrealScalarString.ToCharacters` is the temporary reusable binding primitive
for a raw SurrealQL expression that must preserve a colon-bearing scalar string.
Bind its characters and reconstruct with `array::join($_value_chars, '')` so
SurrealDB 3.x cannot reinterpret `table:id`-shaped values as record ids. Use it
only where the consumed SurrealForge package cannot supply a typed scalar-string
binding; replace it when the package exposes that primitive. Do not recreate the
character-array workaround in individual stores. It maps a missing optional
value to an empty array; callers retain optionality with a separate boolean
parameter because SurrealQL evaluates both `IF` branches.

## §runtime-identity

`SurrealRuntimeConfigurationGuard` keeps the production API on the isolated
`SurrealRuntime` configuration section. Production requires a non-root,
database-scoped user, `AuthenticationScope=Database`, and
`AZOA_SKIP_MIGRATIONS=1`; the API container cannot receive legacy `SurrealDb`
credentials or run the schema tool at boot. The explicit authentication scope
causes the named SurrealForge HTTP client to send `Surreal-Auth-NS` and
`Surreal-Auth-DB`; query selection still uses the package's `Surreal-NS` and
`Surreal-DB` headers. An absent scope remains valid outside Production for
root-based local development. The separate schema job has `SURREALFORGE_*`
credentials and remains an operations
gate until its SurrealDB 3.1.4 permissions are proven live. Built-in database
`EDITOR` is not a DDL-proof role, so do not claim full DDL separation from the
config split alone; see the `surreal-runtime-least-privilege` conductor track.

## §transient-conflict — optimistic-concurrency retry (`SurrealTransientConflict`)

> **Moved to the package.** `SurrealTransientConflict` now lives in
> `SurrealForge.Client.Idempotency` (SurrealForge ≥ 0.2.0). AZOA no longer
> carries a local copy — import `SurrealForge.Client.Idempotency` and use the
> package type. The contract below is unchanged and documents why the seam
> exists; the ledger also exposes it as a config knob
> (`IdempotencyLedgerOptions.RetryOnTransientConflict`).

SurrealDB **3.x** (RocksDB) changed how it handles concurrent writers that
contend the same row under a conditional `UPDATE`. On **1.5.x** those writes
serialized transparently; on **3.x** the engine surfaces a **retryable**
`Transaction conflict: Resource busy ... this transaction can be retried`
error to the loser instead of letting the conditional predicate silently
resolve to `affected == 0`.

This is not a bug to swallow blindly — the engine is explicitly telling us to
retry. `SurrealTransientConflict` is the single shared home for that contract:

- `IsRetryableConflict(Exception)` — message-token match (`Transaction
  conflict` / `Resource busy` / `can be retried`). Message-matched because the
  client raises a plain exception type; the tokens are stable across 3.x.
- `RetryOnConflictAsync<T>(op, ct, maxRetries = 8)` — bounded retry loop with a
  small exponential-ish backoff plus per-attempt jitter to break the herd. On
  retry the **winner's** write has already landed, so a single-winner
  conditional UPDATE loser resolves cleanly to its `affected == 0` / no-op path
  (returns null / `Won == false`) rather than throwing.

**Consumers.** `Core/Idempotency/SurrealIdempotencyStore.TryClaimAsync` (the
original E3 precedent) and the saga single-winner seams in
`Services/Sagas/SurrealSagaStore` (`TryClaimDueStepAsync`, `TrySignalAsync`,
`GetDueStepIdsAsync`). See `Services/Sagas/AGENTS.md` §transient-conflict-retry
for which saga seams are wrapped and why the single-owner transition paths
(Complete / ScheduleRetry / Compensate / DeadLetter / Park) are deliberately
**not** wrapped.

**Only wrap genuinely-contended conditional UPDATEs.** A path where the caller
already holds the row's `InProgress` lease has no concurrent contender, so
wrapping it adds latency-on-error for a conflict that cannot occur.

## §cbor-transport — SurrealForge 1.0.0 SDK/CBOR migration

The API runs on `AddSurrealForgeSdk`, i.e. the official `SurrealDb.Net` SDK and
the **CBOR** wire format. `HttpSurrealConnection` (SurrealForge's own JSON
transport) is gone from both the app and the integration harness. The single
fact that everything below follows from:

> **SurrealDB coerces JSON text into typed columns. It does not coerce CBOR
> text.** A `TYPE record<user>` column accepted the JSON string
> `"user:abc"`; over CBOR it rejects it, and the row it now holds is a genuine
> record link that no string will ever compare equal to.

### §cbor-registration

`Extensions/SurrealDbServiceCollectionExtensions.AddSurrealForge` binds the
configuration section **eagerly** into a `SurrealConnectionOptions` instance,
because `AddSurrealForgeSdk` needs the endpoint and auth scope at *registration*
time (they pick the transport, the auth strategy and the name of the
`HttpClient` the SDK resolves). Consequences:

- **`AuthenticationScope=Root` is now an accepted value**, not just an omission.
  Root Basic Auth is the one credential shape SurrealDB accepts over HTTP.
  `Database` scope over HTTP additionally needs `TokenIssuerUser` /
  `TokenIssuerPassword` (root), because the SDK cannot Basic-Auth a scoped user
  and SurrealForge mints a scoped JWT instead. Pointing `Endpoint` at
  `ws://…/rpc` avoids the root issuer entirely and is the shape that preserves
  `SurrealRuntimeConfigurationGuard`'s least-privilege posture.
- **Test hosts must supply Surreal settings through host configuration.**
  `ConfigureAppConfiguration` on a `WebApplicationFactory` runs *after*
  `Program.cs` has already registered the client, so values supplied only there
  arrive too late and the app silently falls back to
  `SurrealConnectionOptions`' default endpoint. `AZOATestWebApplicationFactory`
  uses `UseSetting` for exactly this reason.
- **Container teardown must be asynchronous.** The SDK's `SurrealDbClient` is
  `IAsyncDisposable`-only; a hand-built provider needs `await using`.

### §cbor-foreign-keys

Three separate mechanisms are needed, because SurrealForge promotes a
`table:id` string to a native record id in exactly one of them.

1. **POCO properties — handled by the package.** A `string` property carrying
   `[References(typeof(T))]` (or `[Id]`) is promoted on write. Every store's
   private wire POCO now carries `[References]` on its record columns; several
   previously relied on JSON coercion and had no annotation at all. `[Column(Type
   = "record<…>")]` alone is **not** enough — the marshaller only consults
   `[Column]` to *veto* promotion (`Type = "string"`), never to enable it.
2. **Query parameters — `SurrealRecordParam`.** `WithParam` values carry no
   attribute, so nothing promotes them. Any parameter compared against a record
   column goes through `SurrealRecordParam.Of` / `.OfLink`. Getting this wrong
   fails **silently**: the predicate matches nothing and the query returns an
   empty set.
3. **The package's own typed surface — handled by the package.**
   `SurrealWriter.Create/Upsert`, `SurrealQuery<T>.Where`, `TypedUpdateOnly
   Builder.Set/Where` and `TypedDeleteOnlyBuilder.Where` bind a record column's
   value as a native record id, via the shared classifier
   `SurrealForge.Client.Query.SurrealRecordLink`. Earlier they bound it as text,
   which failed loudly on write and **silently** on read; a consumer-side shim
   (`SurrealCborRecordLinkCompat`) covered that gap and has been deleted now that
   the package is fixed. Note the classifier deliberately **refuses ambiguous
   ids** — `num:42` (numeric id half) and `⟨…⟩`-escaped ids throw rather than
   link to the wrong row.

`SurrealScalarString.ToCharacters` (§scalar-string-binding) is a JSON-era
workaround: CBOR text is unambiguously text, so SurrealDB can no longer
reinterpret a colon-bearing string as a record id. It is retained because it is
harmless, but it no longer protects against anything.

## generated-statement-scope

SurrealForge 1.2 typed writers reuse parameter names and Combine merges them with
last-write-wins semantics. Scope each generated single statement before composing
multiple writers in one transaction. SurrealGeneratedStatementScope renames only
bound parameter tokens outside quoted strings/identifiers; it rejects batches,
comments, and unbound variables. It accepts trusted generated SQL only, never user
SQL. This preserves the typed writer's record-link marshalling. The single raw
factory reconstructs identical generated SQL with disjoint binding names.

**Capturing a read vs a mutation.** `Assign` captures a statement's result verbatim:
right for `UPDATE ONLY …` (one object or `NONE`), wrong for a read.
`SurrealQuery<T>.Key(id)` emits `SELECT * FROM type::record($_t, $_id)`, and a
`SELECT` returns an **array** even for one record id — so after `Assign`,
`$v.field` is `[value]`, every scalar comparison (`$v.hash != $_hash`) is true,
`crypto::sha256($v.x)` errors, and the fence fails closed on every call. Use
`AssignRecord` for single-record reads: it emits `LET $v = (SELECT …)[0]`, giving
the record or `NONE`, and refuses non-`SELECT` statements. `RequireResult`
accepts both shapes (`NONE` or `[]`). Found 2026-10-16 by the lane B review;
14 `Assign(Key(…))` sites existed at the time.

## identifier-template

`SurrealIdentifierTemplate` is the one home for raw SurrealQL whose *text* (not its
values) varies at runtime: per-index scope names, table/column names chosen by code,
optional predicates. The SurrealForge analyzer (SRDB0001) rejects every interpolated or
concatenated `SurrealQuery.Of` argument, so before round 4 ~36 sites either failed the
build or hid behind per-site `#pragma`s. The template keeps the old call-site shape:

```csharp
var scope = SurrealIdentifierTemplate.Token("_deployment_bundle_" + index);
SurrealIdentifierTemplate.Of($"IF array::len(${scope}) != 1 {{ {SurrealIdentifierTemplate.Throw(Marker)} }}");
```

Safety comes from types, not review: `SurrealIdentifierTemplateHandler` is an
interpolated-string handler whose only `AppendFormatted` overloads take a
`SurrealTemplateToken` (validated `^[a-z0-9_]+$` at construction) or a
`SurrealTemplateFragment` (constructible only through `Fragment($"…")`, `And(…)`,
`Throw(marker)` or `Empty`). A `string`, number or `Guid` hole is a compile error, so no
caller value can reach the text; values still travel only as `WithParam` bindings.
`Throw` validates markers as uppercase letters and underscores (the same rule as
`SurrealGeneratedStatementScope.RequireResult`). The single SRDB0001 suppression is in
`Of`. Migration rule: a rewrite must render byte-identical SQL with identical binding
names — the template only changes how the text is assembled, never what it says. Do not
call `AppendLiteral` by hand. Captures of a typed-generated statement (`LET $v = (<sql>)`)
use `SurrealGeneratedStatementScope.Assign`, not this template. A SurrealForge
identifier-template builder would replace this helper (round-4 plan A-1).

## Policy-sensitive local commits

NodeGovernancePolicyFence reads the singleton version before manager validation. Its
transaction guard takes the same dedicated policy lock as policy updates, then checks
that version or the expected absence. The separate lock preserves option-backed policy
semantics when no database row exists and serializes the first policy insertion with
local effects. Do not create an unrestricted policy row merely to acquire a lock.
ActionExecutionFence joins the same transaction to serialize effects with run/node
cancellation. Receipt replay reads committed evidence without executing again.

DeploymentAuthorityFence locks root and nested bundle access plus complete bound
Holon content in the workflow admission transaction. Holon writes share the entity
lock; row no-op writes serialize mutations that target the same record. Optional
values use explicit NONE predicates, never a null parameter as an absence test.
The root's already deployed immutable version survives newer artifact generation;
nested references additionally require their exact selected descriptor. Existing
invocation receipt replay precedes these live gates and cannot create another run.
# Signing authority transaction fences

`SigningAuthorityFence` composes credential, exact selected grant and wallet checks into the owning admission or first-egress transaction. The raw statements are multi-record locking invariants, not ordinary CRUD. Parameter prefixes isolate the generated snippets: templates are written with `$_sa_<name>` and `Sql()` rewrites them to `$<prefix>_<name>`, which must equal the bound `WithParam(prefix + "_<name>")` key exactly (SurrealQL binds `$x` to parameter `x`). A mismatch leaves every variable unbound, so every fenced batch fails closed (a `SIGNING_AUTHORITY_CHANGED` throw or a query error), which reads like a real authority change and is invisible to deny-path fixtures — only an allow-path fixture against the real database catches it; operator credentials also pin bootstrap credential/session revisions. Workflow callers compare the saved run credential at node admission and the exact node witness at first egress. Explicit legacy participant consent has its own immutable receipt; it never rewrites the original run credential.
## Fungible genesis and captured mutations

`PlatformGenesisFence` composes current validated operator/bootstrap evidence with the original pending platform definition and exact lifecycle identity. Its definition-only branch is used only in finalized accounting, which cannot require a renewed signing session. Activation and reserve/issuance projection commit together. `FungibleCreationAuthorityFence` selects mutually exclusive ordinary signing or genesis purpose; it never treats a node-operator credential as an unrestricted user.

`SurrealGeneratedStatementScope.Assign` captures one trusted typed-generated mutation result within a larger atomic transaction. It preserves the existing quote-aware parameter scoping and rejects invalid variable names, batches and unbound parameters. This allows dependent transaction checks and conditional projection without replacing ordinary typed CRUD with handwritten SQL.

Indexed generated-statement parameter scopes permit lowercase letters, digits and underscores. Numeric suffixes keep repeated typed mutations distinct in one transaction; SQL tokens, comments and unbound variables remain forbidden. Captured transaction-variable names retain their separate restricted validation.

`ExchangeAuthorityFence` preserves the real tenant API-key principal while locking
an independently consenting participant's current watermark, owned wallet and exact
UserExplicit swap grant. Its typed updates supply row locks; the raw assertions
join that credential/participant boundary inside source commitment and egress.
No synthetic participant credential or inferred tenant ownership is created.

`SurrealGeneratedStatementScope.RequireResult(variable, marker)` is the shared abort for a
captured conditional mutation that matched no row (`IF $v = NONE OR $v = [] { THROW 'MARKER' }`).
It validates both tokens, so a raw composition needs no interpolated SQL of its own.
`ExchangeAuthorityFence` uses it for `$_<prefix>_participant`, `_wallet` and `_grant`. Its only
other raw assertion (grant expiry against database time) is a `$$"""` raw string whose single
interpolation is the caller-fixed lowercase prefix. In a single-`$` raw string, `{{` is not an
escape, so literal SurrealQL braces need `$$`.
