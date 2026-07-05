# SurrealDB Entity Convention (C#-first)

**Status:** Rewritten 2026-06-03. Supersedes the Mermaid-source-of-truth
convention that landed 2026-05-22.
**Scope:** All SurrealDB-backed entities in
[Persistence/SurrealDb/Models/](Models/).

---

## 1. Schema is the C# class

Every SurrealDB-backed table lives as a hand-authored `partial class` in
[Persistence/SurrealDb/Models/<Name>.cs](Models/) under the namespace
`AZOA.WebAPI.Persistence.SurrealDb.Models`.

The class carries two layers of attributes:

- **Schema-emit metadata** — `[SurrealTable]`, `[Column]`, `[Assert]`,
  `[Inside]`, `[Default]`, `[Index]`, `[FieldGroup]`, etc., declared
  in [SurrealForge.Client.Schema](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/).
  These drive the `.surql` DDL emit, the flowchart visualization, and
  (eventually) the DBML diff manifest.
- **Wire shape** — `[JsonPropertyName]` for the wire-format column
  name, `[JsonConverter(typeof(JsonStringEnumConverter))]` for
  closed-set enum-typed properties.

The class implements `SurrealForge.Client.ISurrealRecord` (carrying
`SchemaNameConst` + `SchemaName`) so the typed query builder + JSON
serializer round-trip rows correctly.

---

## 2. The three legitimate patterns for additional members

### 2.1 Partial-class siblings — the default

`Persistence/SurrealDb/Models/<Name>.cs` declares the table shape.
Helpers go in **sibling partial-class files** that declare the same
partial in the same namespace:

```csharp
// File: Models/Wallet.Extensions.cs
namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

public partial class Wallet
{
    /// <summary>Guid view of the storage-side Id (Guid('N') hex).</summary>
    public Guid IdGuid
    {
        get => Guid.ParseExact(Id, "N");
        set => Id = value.ToString("N");
    }

    public bool OwnedBy(Guid avatarId) => AvatarId == avatarId.ToString("N");
}
```

**Use partial siblings for:**

- `Guid` ⇄ `string("N")` accessor pairs on `id` / `*_id` fields
- Domain predicates (`OwnedBy(...)`, `IsActive`, `IsTerminal`)
- Static factories that produce a populated POCO from caller inputs

**Avoid:**

- Adding new persisted columns here — add them to the main file with
  `[Column(Order = N, ...)]` so the generator picks them up.
- Heavy domain logic — that belongs in the manager.
- Anything that touches another aggregate — composite views belong in
  request/response DTOs (see §2.3).

### 2.2 In-memory transients — `Models/<Aggregate>/<Type>.cs`

Types that are never persisted (execution context, per-node config
unions, in-flight state machines) live as plain C# classes in
`Models/<Aggregate>/<Type>.cs` under the **app's** `AZOA.WebAPI.Models.*`
namespace (NOT the persistence namespace):

```csharp
namespace AZOA.WebAPI.Models.Quest;

using AZOA.WebAPI.Persistence.SurrealDb.Models;

public sealed record QuestNodeExecutionContext(
    QuestRun Run,
    QuestNode Node,
    IReadOnlyDictionary<Guid, QuestNodeExecution> UpstreamOutputs);
```

### 2.3 Request / response DTOs — `Models/Requests/`

Caller-facing inputs and outputs live as plain classes in
`Models/Requests/<Aggregate>Requests.cs`. They are intentionally
**not** the same type as the storage POCO — the API surface should
not leak storage-side concerns (enum nesting, JsonElement fields, etc.):

```csharp
namespace AZOA.WebAPI.Models.Requests;

public class DappSeriesUpdateModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? SharedConfig { get; set; }
}
```

---

## 3. What this looks like in practice

| Question | Answer |
|---|---|
| "Where do I add a new persisted column?" | Add a `[Column(Order = N, Type = "...")]`-decorated property to the POCO in [Models/](Models/). Build → `.surql` regenerates. |
| "Where do I add `Guid IdGuid { get; set; }`?" | A sibling partial-class file under [Models/](Models/), same namespace. |
| "Where do I add a `QuestStartedNotification` DTO?" | `Models/Requests/QuestRequests.cs`. References `AZOA.WebAPI.Persistence.SurrealDb.Models.Quest` by type if it needs to. |
| "Where do I write `ComposeAsync(...)` validation logic?" | The manager (`Managers/DappCompositionManager.cs`). Never on the entity. |
| "What about a *computed* `IsActive` from latest QuestRun?" | Manager-level method, not a partial. The entity doesn't get to query a different aggregate. |
| "How do I regenerate the `.surql` files?" | `surrealforge generate-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll`. Or just run the byte-equivalence test — failures point at drift. |

---

## 3.5 POCO authoring walkthrough

Use this four-step flow any time you add or modify a persisted table.

**Step 1 — Edit the POCO in `Persistence/SurrealDb/Models/<Name>.cs`**

```csharp
[SurrealTable("star_odk", Schemafull = true)]
[Slice("domain")]
public partial class StarOdk : ISurrealRecord
{
    public const string SchemaNameConst = "star_odk";
    public string SchemaName => SchemaNameConst;

    [Column(Order = 1, Type = "string")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [Column(Order = 2, Type = "string")]
    [References(typeof(Avatar), Optional = false)]
    [JsonPropertyName("avatar_id")]
    public string AvatarId { get; set; } = "";

    [Column(Order = 3, Type = "int")]
    [Default("0")]
    [JsonPropertyName("karma_score")]
    public int KarmaScore { get; set; }
}
```

Key decoration rules:
- Class: `[SurrealTable("<name>", Schemafull = true)]` + `[Slice("<aggregate>")]`
- Class must implement `ISurrealRecord` (provides `SchemaNameConst` + `SchemaName`)
- Each persisted property: `[Column(Order = N, Type = "<surreal-type>")]`
- Optional FK column: `[References(typeof(Target), Optional = true/false)]`
- Closed-set column: `[Inside("A", "B", "C")]` with a matching nested enum (see §4)
- Non-null default: `[Default("<value>")]`
- Index: `[Index(Fields = new[] { "field" }, Unique = true)]` at class level
- Computed / helper properties belong in a sibling partial — do NOT add `[Column]` to them
- Wire name: `[JsonPropertyName("<name>")]` on every persisted property

**Step 2 — Build the project**

```
dotnet build
```

The build must succeed before the generator can reflect over the assembly.

**Step 3 — Regenerate `.surql` and flowchart artifacts**

```
surrealforge generate-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
surrealforge flowcharts-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
```

The first command runs `AttributeSchemaScanner` → `SurqlEmitter` and overwrites
the `.surql` files in `Persistence/SurrealDb/Generated/Schemas/`. The second
command runs `AttributeSchemaScanner` → `MermaidFlowchartEmitter` and overwrites
the flowchart files in `Persistence/SurrealDb/Generated/Flowcharts/`. Both
commands source from the same compiled assembly; both must be run when the POCO
changes affect the flowchart grouping (`aggregate` field on `[SurrealTable]`).

**Step 4 — Confirm the generated artifacts**

- `.surql` file appears (or updates) at
  `Persistence/SurrealDb/Generated/Schemas/<name>.surql`
- The relevant slice flowchart updates at
  `Persistence/SurrealDb/Generated/Flowcharts/<slice>.flowchart.mermaid`
- Run the acceptance gate to confirm no drift:

  ```
  dotnet test --filter "FullyQualifiedName~AttributePocoByteEquivalenceTests"
  ```

  A passing run means every committed `.surql` file is byte-for-byte reproducible
  from the current POCO definitions. A failing run means either a POCO was edited
  without regenerating, or a `.surql` was hand-edited (see §8, anti-pattern 1).

> **Do not edit anything under `Persistence/SurrealDb/Generated/`.** Every file
> there is overwritten on the next `generate-from-assembly` run. The POCOs in
> `Models/` are the only authoring surface.

---

## 4. Closed-set enum fields

Fields with `[Inside("A", "B", "C")]` should be typed to a **nested
enum** inside the partial class. The wire shape uses
`JsonStringEnumConverter` so the JSON value is the enum member name:

```csharp
public enum StatusKind
{
    Initiated, Locked, AwaitingVAA, VAAReady, Redeeming, Completed,
    Failed, Refunded, Reversing,
}

[Column(Order = 10, Type = "string")]
[Inside("Initiated", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed",
        "Failed", "Refunded", "Reversing")]
[JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
public StatusKind Status { get; set; }
```

The `[Inside]` value list and the enum members **must** match in
spelling. Drift is caught by the byte-equivalence test
(`AttributePocoByteEquivalenceTests`) because the emitted `ASSERT
INSIDE [...]` clause has to match the committed `.surql`.

---

## 5. RELATE-edge tables

Tables that exist purely as graph edges (e.g. `forked_from`,
`executes` in the quest graph) carry the `[RelateEdge(typeof(From),
typeof(To))]` class-level attribute. They have exactly two columns,
`in` and `out`, typed `string` and storing record-id references:

```csharp
[SurrealTable("forked_from", ..., Schemafull = true)]
[RelateEdge(typeof(QuestRun), typeof(QuestRun))]
[Slice("quest")]
public partial class ForkedFrom : ISurrealRecord
{
    [Column(Order = 1, Type = "string"), JsonPropertyName("in")]
    public string In { get; set; } = "";

    [Column(Order = 2, Type = "string"), JsonPropertyName("out")]
    public string Out { get; set; } = "";
}
```

The `[RelateEdge]` attribute is informational only — it does not
affect the `.surql` emit. The flowchart emitter uses it to render the
table as an arrow (not a node) on the visualization.

---

## 6. Index-only / virtual tables

Tables that exist only to host HNSW vector indexes (e.g.
`hnsw_holon_embedding`) carry `[VirtualTable]` + `[Slice("_skip")]`.
They emit the `DEFINE TABLE` + `DEFINE FIELD` DDL but are not
persisted with rows — the application writes the vector via raw
SurrealQL on the parent table's column.

---

## 7. Generated output

> **Canonical counts as of 2026-06-10:** **26 POCOs** in
> `Persistence/SurrealDb/Models/` (`ApiKey`, `Avatar`, `BridgeTx`,
> `ConsumedVaaLedger`, `DappSeries`, `DappSeriesQuest`, `Executes`,
> `ForkedFrom`, `HnswHolonEmbedding`, `HnswQuestEmbedding`, `Holon`,
> `IdempotencyKeyStore`, `NftOwnership`, `OperationLog`, `Quest`,
> `QuestDependency`, `QuestEdge`, `QuestNode`, `QuestNodeExecution`,
> `QuestNodeTemplate`, `QuestRun`, `QuestTemplate`, `SagaSteps`, `StarOdk`,
> `SwapState`, `Wallet`) producing **26 `.surql` schema files** and
> **6 slice flowcharts** (`bridge`, `dapp_composition`, `identity`,
> `quest`, `quest_templates`, `wallet_nft`) plus the `domain.flowchart.mermaid`
> master — **7 generated files total** under `Persistence/SurrealDb/Generated/Flowcharts/`.

The build emits derived artifacts to `Persistence/SurrealDb/Generated/`:

```
Persistence/SurrealDb/
    Models/                    # hand-authored, canonical
        Wallet.cs
        Wallet.Extensions.cs
        ...
    Generated/                 # auto-generated; DO NOT EDIT
        Schemas/
            wallet.surql
            ...
        Flowcharts/
            wallet_nft.flowchart.mermaid
            domain.flowchart.mermaid
        Dbml/
            schema.dbml        # opt-in via SurrealForgeOptions
```

Regenerate via:

```
surrealforge generate-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
surrealforge flowcharts-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
```

The output paths can be overridden via `SurrealForgeOptions.Generation.GeneratedPath`
in `appsettings.json`.

---

## 8. Anti-patterns

1. **Hand-editing a `.surql` file in `Generated/Schemas/`.** They are
   regenerated from the POCO on every build; manual edits are
   overwritten silently. Edit the POCO instead.
2. **Adding a `[SurrealTable]` POCO without a corresponding
   `Generated/Schemas/<table>.surql` file.** The byte-equivalence test
   will fail with "missing committed .surql." Regenerate.
3. **Splitting a single aggregate across two POCO classes.** One
   `[SurrealTable]` = one aggregate. Multiple partial-class files are
   fine; multiple top-level types are not.
4. **A partial extension that calls another aggregate's manager.**
   Put cross-aggregate logic in the calling manager, not the entity.
5. **A partial extension that contains business rules.**
   State-machine guards, validation, side effects — all in the
   manager, not the entity.

---

## 9. Foreign keys + RELATE edges (added 2026-06-05)

POCO columns that point at another table use
`[References(typeof(TargetPoco))]`. The schema emits
`record<target_table>` (or `option<record<target_table>>` with
`Optional = true`), and the slice flowchart automatically draws the
edge with cardinality `N:1` or `N:0..1`.

RELATE-edge tables (currently `ForkedFrom`, `Executes`) carry
`[RelateEdge(typeof(From), typeof(To))]` at the class level. The
emitter renders them as
`DEFINE TABLE x TYPE RELATION FROM y TO z SCHEMAFULL;`.

Closed-set enum columns marked with `[Inside("A", "B")]` emit a
`DEFINE PARAM IF NOT EXISTS $<table>_<column>` block at the top of the
file plus `ASSERT $value INSIDE $<table>_<column>` on the column. The
master flowchart's enum legend surfaces every closed set.

### Known follow-up: SurrealDB adapter wire-format migration

Flipping FK columns to `record<target>` changed the wire format from
the raw hex string (`"abc123"`) to the prefixed form
(`"avatar:abc123"`). The store adapters in
[`Providers/Stores/Surreal/`](../../Providers/Stores/Surreal/) still
write the bare hex form via `ToSurrealId(guid)`; SurrealDB will reject
those writes at runtime against the new schema. The adapter rewrite
(introduce `ToSurrealRecordId(table, guid) -> "table:hex"` and update
every FK assignment in `ToPoco` / `FromPoco` mapping helpers + every
LINQ `Where(x => x.AvatarId == hexString)` clause to compare against
the prefixed form) is a separate session of work. Unit tests pass; the
gap surfaces only at integration-test time. The flowchart + legend +
DDL emission work shipped on 2026-06-05 do NOT depend on this
migration completing.

## 10. Applying schemas to a live SurrealDB (`surrealforge up`)

The canonical entry point for "bring the deployed DB in sync with the
schema" is:

```
surrealforge up \
    --connection http://127.0.0.1:8000 \
    --user root --pass root \
    --namespace azoa --database azoa
```

Two-phase apply:

1. **Phase 1: schemas** — every `.surql` under
   [`Generated/Schemas/`](Generated/Schemas/) is applied in lexical
   (file-name) order. Idempotent on re-run because the emit shape uses
   `DEFINE TABLE/FIELD/INDEX/PARAM IF NOT EXISTS`.
2. **Phase 2: migrations** — every `.surql` under
   [`Migrations/`](Migrations/) is applied in lexical (timestamp)
   order. Hand-authored data backfills, one-shot fixes, dev seed data —
   see [`Migrations/README.md`](Migrations/README.md) for naming +
   authoring rules.

Both phases funnel through the same `schema_migration` ledger (table
created automatically on first run, keyed by file name + SHA-256
checksum). Re-running `up` is a no-op when nothing changed.

### Namespace + database bootstrap

The runner creates the configured namespace + database on first run if
they don't already exist:

```
DEFINE NAMESPACE IF NOT EXISTS <ns>;
USE NS <ns>;
DEFINE DATABASE IF NOT EXISTS <db>;
```

The `--namespace` / `--database` CLI flags (or the
`SURREALFORGE_NS` / `SURREALFORGE_DB` env vars) supply the names. No
out-of-band setup step is required on a fresh SurrealDB server.

### Flags

| Flag | Default | Purpose |
|---|---|---|
| `--schemas-dir <path>` | `Persistence/SurrealDb/Generated/Schemas` | Phase-1 source |
| `--migrations-dir <path>` | `Persistence/SurrealDb/Migrations` | Phase-2 source |
| `--dry-run` | (off) | Plan without writing |
| `--force` | (off) | Overwrite recorded checksum on mismatch (use sparingly; see migrations README) |
| `--applied-by <s>` | `surrealforge/cli` | Identity recorded in each `schema_migration.applied_by` row |
| `--connection <url>` | (required) | SurrealDB HTTP endpoint, e.g. `http://127.0.0.1:8000` |
| `--user <s>` / `--pass <s>` | (required) | Basic-auth credentials |
| `--namespace <s>` / `--database <s>` | (required) | Target scope. Created automatically if missing. |

### `migrate` command matrix

| Command | Status |
|---|---|
| `surrealforge migrate up` | Implemented (also: `surrealforge up` — the two-phase apply documented above) |
| `surrealforge migrate status` | Implemented |
| `surrealforge migrate dry-run` | Implemented |
| `surrealforge migrate reset` | Implemented |
| `surrealforge migrate down` | Stubbed — manual rollback only (intentional pre-launch) |

### Status check

```
surrealforge migrate status \
    --connection http://127.0.0.1:8000 \
    --user root --pass root \
    --namespace azoa --database azoa
```

Lists every applied file with checksum + apply timestamp from the
deployed `schema_migration` ledger.

### Idempotent contract verification

The integration test
[`MigrationRunnerLiveTests`](../../tests/SurrealForge.Schema.Tests/Migration/MigrationRunnerLiveTests.cs)
applies every committed `.surql` against a live SurrealDB instance,
asserts the ledger lands one row per file, then re-runs the same set
and asserts every plan item classifies as `Skip` (the canonical
idempotent no-op). Tagged `[Trait("Category", "Live")]` so CI lanes
without SurrealDB can opt out via `--filter "Category!=Live"`.

## 11. References

- Attribute layer: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/SurrealAttributes.cs`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/SurrealAttributes.cs)
- Annotation reference: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/ANNOTATIONS.md`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/ANNOTATIONS.md)
- Configuration shape: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/SurrealForgeOptions.cs`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/Schema/SurrealForgeOptions.cs)
- Schema scanner: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/AttributeSchemaScanner.cs`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/AttributeSchemaScanner.cs)
- `.surql` emitter: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/SurqlEmitter.cs`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/SurqlEmitter.cs)
- Flowchart emitter: [`https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/MermaidFlowchartEmitter.cs`](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/Generator/MermaidFlowchartEmitter.cs)
- Byte-equivalence test: [`tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs`](../../tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs)
- Originating ADR: [`conductor/tracks/surrealql-toolkit/DESIGN-mermaid-portfolio.md`](../../conductor/tracks/surrealql-toolkit/DESIGN-mermaid-portfolio.md)
