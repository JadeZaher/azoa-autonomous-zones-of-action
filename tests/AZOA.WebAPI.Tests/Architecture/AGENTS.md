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
Each lane classifies only the routes and stores it owns.

- **Route rows:** `route` (`"VERB /template"`, compared after normalizing case and constraints),
  `controller`, `action`, `class` (`value-moving` | `non-value`), `scope` (required when value-moving),
  `enforcement`, plus optional `childCredentialScope` and `childSigningScopes`. Every POST/PUT/DELETE/PATCH
  action must have exactly one row. A row that names a missing action is stale and fails. The
  `[DelegatedChildCredential]` endpoints and the delegable scope vocabulary must equal the inventoried
  child-credential rows.
- **Store rows:** `store` (interface name, or the concrete `Surreal*Store` when it has no interface),
  `method`, `tenantCheck`. An owned-resource method is any public method on an `Interfaces.Stores`
  interface, or on an interface-less `Surreal*Store`, that takes an ownership-named parameter (`userId`,
  `tenantId`, `actor`, ...) or takes or returns a type with an owner/tenant property. Overloads share one
  row.

Both "every ... is classified" tests fail until every lane's inventory has merged, which is intended: they
are the sweep's completeness gate.
