# Architecture ratchets

`CodeStyleDebtBudgetTests` is a non-regression ceiling, not an endorsement of
the current debt. It scans the production source roots and rejects new raw
mutations, dynamic query construction, or blanket `catch (Exception)`. It also
keeps a per-file raw-query/catch ceiling for Surreal stores. A migration may
lower or remove an entry; raising a ceiling requires an active track waiver and
secondary review.

The raw-mutation scanner recognizes ordinary, verbatim, and raw string literals
whose first mutation is `CREATE`, `UPSERT`, `UPDATE`, or `DELETE`, including a
mutation assigned through a leading `LET`. Multi-table transaction components
are counted too: their atomicity may justify a retained entry, but it does not
exempt them from the inventory.

## Retired vocabulary ratchet

`RetiredVocabularyGuardTests` keeps D28 (`Avatar`→`User`, `Quest`→`Action`), the
`10f4c16` STAR→ActionGraphBundle rename, and the removal of game vocabulary from
regressing. It exists because both renames passed their own gates and then
residue grew back in code written afterwards. Nothing enforced the rename once
its sweep finished.

It scans what ships: the backend roots, `Properties/`, `deploy/`, the
`Dockerfile`, `railway.json`, root `appsettings*.json`, generated `.surql`, and
the SDK's `src`, `package.json` and `README.md` (the npm page). It also scans
`frontend/src` (colocated frontend tests included) and `frontend/public`.
Comments count, and so do file and directory names: a `quests/` route folder
would ship a `/quests` URL. The `tests/` projects, docs and the conductor bundle
are out of scope, because they narrate the rename and quote consumer vocabulary.
Every scanned root must yield files, so renaming a directory fails loudly
instead of silently dropping coverage.

Each identifier is split into acronym-plural, acronym and camel words before
matching. So `request` and `questionnaire` never trip `quest`, `START` and the
lucide `Star` icon never trip `STAR`, and `ODKs` stays one word. The pre-10f4c16
shapes are matched explicitly: `Star` followed by `Generate`, `Deploy`,
`Manager`, `Store`, `Service`, `Controller`, `Dapp`, `Odk` or `Id`; glued
acronyms like `XSSODK`; and `api/star` routes. A bare `Star` + capital is
deliberately not matched, because `StarTable` lowercases to "startable". The
matcher is pinned by a table-driven theory of real positives and lookalikes.

`CompatibilityBaselines/` and `Migrations/` are skipped because SurrealForge
ledgers their SHA-256. Shipped bytes there can never change, and a forward
migration may have to name a retired table to remove it. The flip side bites
in POCO annotations: an `Aggregate`/`SurrealNote` string is emitted into a
generated `.surql`, so changing its vocabulary changes that file's checksum.
Any database that already applied the old bytes then stops on drift, and a
dev volume needs `dev-up -Reset`.

The base-ui `Avatar` primitive is exempt at token level, not file level. Only
its wrapper, `ui/avatar.tsx`, is skipped whole. In a file that imports it, the
`Avatar` component tokens and the import path pass, and every other word is
still checked. The residual sweep that cleared the tree, with its per-line
review manifest, lives in
`conductor/tracks/terminology-neutralization-cutover_20260905/residual-sweep/`.

## Federation boundary ratchet

`FederationBoundaryTests` keeps the activation gate enforceable: before recorded,
machine-verifiable activation evidence proves three independent production
operators and a real counterparty use case, the AZOA node must not reference any
`Azoa.Commons.*` or `Azoa.Holochain.*` package, a Holochain runtime/conductor API,
or the prohibited NextGen/HoloNET prior-art client. Post-gate, a reviewed ratchet
change may permit only the thin `Azoa.Commons.Client` facade; Contracts and Runtime
remain prohibited direct references. The runtime remains in the separate
`AZOA.Commons` repository. The normative package and protocol shape lives in
`conductor/tracks/federation-v2/holochain-dotnet-bridge-contract.md`.
The source scan blocks any `Holochain` or `Azoa.Holochain` implementation
namespace, not merely a current allowlist of known runtime types.

## enum-storage

`EnumStorageConverterTests` reflects over every `[SurrealTable]` type in the API assembly and
requires each enum-typed property (or its enum type) to carry
`[JsonConverter(typeof(JsonStringEnumConverter))]`. Without it `SurrealWriter` stores the C# name
while SurrealForge 1.3.0 typed reads bind the camelCase form, so a typed filter silently misses
writer-written rows (found for `NodeFeeSettlement`/`NodeFeeAtomicGroup`, round 4 W0). Storage
mappers convert enums by name (`Enum.Parse(x.ToString())`), never by ordinal cast.

## value-gate-coverage

`ValueGateCoverageTests` fails when any production type takes `IKycGateService` as a constructor
dependency. That interface is the bare KYC decision; value paths use `IValueAccessService` or
`IRealValueKycGate`, which the host wraps in the reversal risk hold (`Services/Kyc/AGENTS.md`
§Value-access decision). Before the round-4 fix batch every `IValueAccessService` path skipped the
hold this way.

## Gate inventories

`RouteAuthorizationGateTests` and `CrossTenantGateTests` load every lane's
`Inventories/RouteAuthorization/*.json` and `Inventories/CrossTenant/*.json` (each file is a JSON array).
After round 4 (RV-L4-04, X-3..X-5) the inventories have a single owner (`r4/fix-security`); later lanes add
their own rows in their lane file.

- **Route rows:** `route` (`"VERB /template"`), `controller`, `action`, `class` (`value-moving` |
  `non-value`), `scope` (required when value-moving), `enforcement`, `policies`, plus optional
  `childCredentialScope` and `childSigningScopes`. The key is verb + normalized template + action, so an
  overload, a second `[HttpX]` template or a second controller `[Route]` prefix (an `api/operator` alias)
  each need their own row. `policies` is the action's real authorization: class and method
  `[Authorize(Policy)]` names, `authenticated` for a bare `[Authorize]`, `anonymous` for
  `[AllowAnonymous]`, or `none`. The gate compares it to the reflected attributes and fails on any
  difference, and it fails when `enforcement` names a policy the action does not carry. That is what makes a
  row a claim about enforcement rather than free text: removing or swapping an `[Authorize]` turns the gate
  red until the row is re-reviewed.
- **Verb and minimal-API completeness:** every controller action must declare its verbs
  (`IActionHttpMethodProvider`: `[HttpX]` or `[AcceptVerbs]`); a `[Route]`-only action answers every verb
  and fails. A `Map{Post,Put,Delete,Patch,Methods}` call in production code fails too, because the
  controller inventory cannot see it.
- **Store rows:** `store`, `method`, `tenantCheck`, one method per row (no composite `"A / B"` rows). The
  scanned contracts are every public interface named `*Store` or `*Ledger` anywhere in the API assembly,
  plus a concrete `Surreal*Store`/`*Ledger` that implements none of them. A row keyed on a concrete class
  that implements a scanned interface is stale: key it by the interface. An owned-resource method takes an
  ownership-named parameter (`userId`, `tenantId`, `actor`, ...) or takes or returns a type with an
  owner/tenant property. Overloads share one row. `tenantCheck` starts with how isolation holds:
  `query-scoped`, `query-pinned`, `caller-checked` (name the caller and the comparison), `keyed`,
  `fenced`, `worker-only`, `operator-only`, `node-level`, or `internal` (no production caller).

**Rule for every lane that adds a route or a store method:** add its row in the same change, with the
real `policies`, and run both gate classes by filter. The "every ... is classified" tests are the
completeness gate and must stay green on the merged tree.

## surrealql-source-rules

`SurrealQlSourceRulesTests` scans production C# (`CSharpSource`: comment-free code plus string literals with
offsets, so raw/verbatim/interpolated SQL is seen) for the round-4 fail-closed classes. Every rule is pinned by a
pre-fix snippet that must be red and a post-fix snippet that must be green (the mutation check). Rules: no `;` in
`SurrealQuery.Of` literals; `Of` takes only literal text (or a same-file `const`, or a ternary of literals) outside
`Core/Surreal/{SurrealIdentifierTemplate,SurrealGeneratedStatementScope,SigningAuthorityFence}.cs`; SRDB0001 is
suppressed only there (pragma/`SuppressMessage`; project files never silence it); no `Assign(Key(…))`; no ordinal
enum casts (target resolved by reflection to an app enum); prefer an absolute result index captured before a
top-level RETURN (RV-L4's 3.1.4 probe: BEGIN and COMMIT keep their slots, so this is robustness, not a live defect);
nothing between a top-level RETURN and COMMIT (3.1.4 skips it); every typed `UpdateOnly`/`DeleteOnly` carries a
`Where` (1.3.0 `Build()` throws without one); no bare id into a `[References]` column — POCO initializers and typed
`Set`, types resolved the way the compiler would (aliases, usings, nested types), locals followed one level; POCOs
whose store links ids in `ToStorage` are skipped only if that store provably links every reference column; forward
migrations use `DEFINE … OVERWRITE`; `tenant_payout_*` rows change only through the D-8 ledger commit.

`KnownViolations` is a ratchet of reviewed residue per (rule, file) with an owner: a new site fails, a fixed site
fails until its entry is lowered. The round-4 entries are W3 fix-batch items (lane L6 record §Needs other lane).

## transaction-scope-gate

`TransactionScopeGateTests` mechanizes `round3-transaction-scope-review.md`: every member that opens `BEGIN` reaches
a `COMMIT` in itself, a callee or a caller (call-graph fixpoint; delegate-committed openers listed with a reason);
every batch family that decides carries a THROW guard (`THROW`, `RequireResult`, fence calls; guardless batches
listed with a reason); egress writes (`AuthorizedAt`/`ExternalExposedAt`) happen only in members that call the
family's operation-row committer (swap `CommitStageAsync`, WTT `CommitAsync`); and ratcheted files carry a
`// lock: <row>` comment before every in-batch read in the same member.
