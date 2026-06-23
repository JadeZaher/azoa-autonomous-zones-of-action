# DESIGN — C#-first SurrealDB schema authoring (AZOA-scoped)

**Status:** Design doc. Created 2026-05-27, revised 2026-05-27 evening
after the C#-first pivot. **Not yet implemented.** Scope is narrowly
AZOA-internal — no public-toolkit framing, no umbrella program. If
the AZOA-internal version proves valuable enough to extract later,
that's a future conversation.

**Supersedes:** the original Mermaid-portfolio direction in this same
file (git history preserves it as commit `2326714`). See [§ Why the
pivot](#why-the-pivot) below for the reasoning.

## TL;DR
Schema is authored as **decorated C# POCOs in this repo**. Attributes
declare the shape (table name, FK, indexes, ASSERT inputs, state
machine binding). Partial classes carry executable validation +
domain helpers. The Roslyn generator that today emits POCOs from
Mermaid is **inverted**: it now scans the C# attribute surface and
emits the `.surql` schema + a single DBML file (for diff against the
deployed DB) + optionally Mermaid views as downstream artifacts.

The hand-written `.mermaid` source files are retired once the C#
surface is complete + verified byte-equivalent on `.surql` output.

## Why the pivot

The Mermaid-portfolio direction (multi-diagram authoring across
`schema/*.mermaid` files) was the right *response* to the original
direction's awkwardness, but it was solving the wrong layer. Three
real problems with the Mermaid authoring surface:

1. **No IDE story.** Authors are in C# all day. Switching to Mermaid
   gives up autocomplete, "find references" on a column, refactoring
   safety, debugger access. The cost compounds every edit.
2. **Validation lives elsewhere.** FluentValidation classes sit in a
   different folder from the model. The Mermaid surface couldn't host
   executable validators; the C#-first surface naturally does.
3. **Bespoke comment annotations are not a real language.**
   `%% @surreal.slice "..."` parsed strictly is still a stringly-typed
   metadata layer with no compile-time guarantees. C# attributes give
   us type-checked metadata + IntelliSense + analyzer hooks for free.

DBML enters as the **diff/diagram target** because it has first-class
FK syntax (`Ref: posts.user_id > users.id`) — Mermaid's FK is an
arrow between boxes, graphical not semantic. Diffing DBML is
data-shaped; diffing Mermaid is picture-shaped.

## Authoring surface

### Attributes
Declarative metadata the generator reads. Attribute layer carries
*shape*, never *code paths* (no closures-in-attribute-args, no
stringly-typed validator method names beyond well-known partial-method
hooks).

```csharp
// Persistence/SurrealDb/Models/Quest.cs (hand-authored)
[SurrealTable("quest"), Slice("quest"), Schemafull]
public partial class Quest
{
    [Id]
    public Guid Id { get; set; }

    [Column, References<Avatar>]
    [Index("quest_by_avatar")]
    public Guid AvatarId { get; set; }

    [Column, MaxLength(200)]
    public string Name { get; set; } = "";

    [Column, Optional]
    public string? Description { get; set; }

    [Column, References<QuestTemplate>(Optional = true)]
    [Index("quest_by_template")]
    public Guid? TemplateId { get; set; }

    [Column, References<DappSeries>(Optional = true)]
    [Index("quest_by_dapp_series")]
    public Guid? DappSeriesId { get; set; }

    [Column]
    public Dictionary<string, string> Metadata { get; set; } = new();

    [Column, EmbeddingVector(Dimension = 384), CSharpSkip]
    public float[]? Embedding { get; set; }

    [Column]
    public DateTime CreatedDate { get; set; }

    // Navigation properties — generator infers cardinality from
    // the inverse side (QuestNode.QuestId is required, so 1:M).
    [Relation(inverseOf: nameof(QuestNode.QuestId))]
    public List<QuestNode> Nodes { get; set; } = new();

    [Relation(inverseOf: nameof(QuestRun.QuestId))]
    public List<QuestRun> Runs { get; set; } = new();
}
```

### Partial class — validation + domain helpers
The same Quest type, in a sibling partial-class file, carries
executable logic the attribute layer cannot:

```csharp
// Persistence/SurrealDb/Models/Quest.Validation.cs (hand-authored)
public partial class Quest
{
    // Well-known partial method the generator wires into the
    // validation pipeline. The generator emits the calling site;
    // authors fill in the body.
    static partial void OnValidating(ValidationContext<Quest> ctx)
    {
        ctx.RuleFor(q => q.Name).NotEmpty().MaximumLength(200);
        ctx.RuleFor(q => q.AvatarId).NotEmpty();
        ctx.RuleFor(q => q.Metadata)
           .Must(m => m.Count <= 50)
           .WithMessage("metadata limited to 50 keys");
    }

    // Domain helpers stay on the same partial class.
    public bool IsInstantiatedFromTemplate => TemplateId.HasValue;
    public bool IsPartOfDappSeries => DappSeriesId.HasValue;
}
```

The Mermaid-era partial-class extension pattern from
`Persistence/SurrealDb/CONVENTION.md` carries forward unchanged — same
namespace, sibling files, additive.

### State machines via flowchart-shaped attributes
The driver that motivated `flowchart` blocks in the prior design is
expressible as attributes on a dedicated enum + a static transition
declaration:

```csharp
// Persistence/SurrealDb/Models/BridgeStatus.cs (hand-authored)
[StateMachine(Of = typeof(BridgeTx), Field = nameof(BridgeTx.Status))]
public enum BridgeStatus
{
    Initiated,
    Locked,
    AwaitingVAA,
    VAAReady,
    Redeeming,
    Completed,
    Failed,
    Refunded,
    Reversing,
}

public static partial class BridgeStatusTransitions
{
    // Hand-authored transition table. Generator emits
    // IsValidTransition + the .surql ASSERT INSIDE list + the enum
    // member list, all from this single declaration.
    public static readonly IReadOnlyList<(BridgeStatus From, BridgeStatus To)> Allowed = new[]
    {
        (BridgeStatus.Initiated,   BridgeStatus.Locked),
        (BridgeStatus.Locked,      BridgeStatus.AwaitingVAA),
        (BridgeStatus.Locked,      BridgeStatus.Failed),
        (BridgeStatus.Locked,      BridgeStatus.Reversing),
        (BridgeStatus.AwaitingVAA, BridgeStatus.VAAReady),
        (BridgeStatus.VAAReady,    BridgeStatus.Redeeming),
        (BridgeStatus.Redeeming,   BridgeStatus.Completed),
        (BridgeStatus.Reversing,   BridgeStatus.Refunded),
    };
}
```

**Generated:**
- `BridgeStatusTransitions.IsValid(from, to)` — exhaustive switch.
- `.surql` `ASSERT $value INSIDE ["Initiated", "Locked", ...]` on
  the bound field (members enumerated from the enum).
- Optionally: a test scaffold asserting every disallowed transition
  fails `IsValid`.

The three-way drift the prior design called out (C# switch vs
SurrealDB ASSERT vs prose note) collapses into the one `Allowed`
declaration. No new diagram format needed.

### Guardrails / requirements via attributes
The `requirementDiagram` direction also collapses to attributes:

```csharp
// Persistence/SurrealDb/Guardrails/Guardrails.cs (hand-authored)
public static class Guardrails
{
    public const string G2_ExactlyOnce = "G2: concurrent claim attempts produce exactly one winner";
    public const string G6_Schemafull  = "G6: every value table is SCHEMAFULL";
    // ... G1, G3, G4, G5, G7
}

// On the type that satisfies it:
[Satisfies(Guardrails.G2_ExactlyOnce)]
[Satisfies(Guardrails.G6_Schemafull)]
public partial class IdempotencyKeyStore { ... }

// On the method that proves it:
[Satisfies(Guardrails.G2_ExactlyOnce, VerifyMethod = typeof(G2_IdempotencyTocTouTest))]
public Task<...> TryClaimDueStepAsync(...) { ... }
```

**Generated:** a markdown traceability table in
`docs/RESIDUAL-RISK-RUNBOOK.generated.md` listing every guardrail +
every class/method/test that satisfies it + file references. Drift
detection: if a `[Satisfies]` references a `VerifyMethod` type that
no longer exists, the generator warns.

## Generator: source-gen flipped

Today the Roslyn `IIncrementalGenerator` at
[packages/Azoa.SurrealDb.SourceGen/](../../../packages/Azoa.SurrealDb.SourceGen)
reads `.mermaid` files via `AdditionalTextsProvider` and emits POCOs.
The pivot **inverts the input source**: read the C# attribute surface
via `ISymbolProvider` (already available in Roslyn generators) and
emit the artifacts the Mermaid pipeline used to produce.

### Outputs (per build)
1. **`.surql` schema files** — `Persistence/SurrealDb/Schemas/NNN_<table>.surql`,
   one per attributed POCO. Same format the Mermaid generator emits today;
   exit byte-equivalent for the migration test.
2. **DBML manifest** — `docs/schema.dbml`, single file capturing every
   table + every FK ref + indexes + notes. Renderable on dbdiagram.io;
   diffable against deployed DB via a future `db pull → dbml` step.
3. **Validation calling sites** — generated `<Type>.Validation.g.cs`
   files that wire `OnValidating` partial methods into the application's
   validation pipeline. Authors fill in the bodies; the generator owns
   the registration.
4. **State-machine code** — `<Enum>Transitions.g.cs` with
   `IsValid(from, to)` + enum exhaustiveness assertions.
5. **Guardrail runbook** — `docs/RESIDUAL-RISK-RUNBOOK.generated.md`.
6. **Mermaid view (optional, downstream)** — if the slice-diagram
   surface is still wanted, generate it from the attribute scan rather
   than from `.mermaid` source files. Same `AggregateEmitter` shape,
   different input.

### Run model
- **Build-time** (Roslyn): every artifact whose consumer is C# code
  (validation calling sites, transition tables) emits during
  `dotnet build`. Zero-config, always current.
- **CLI** (`dotnet run --project packages/Azoa.SurrealDb.Schema -- generate`):
  the artifacts whose consumer is a file checked into git (`.surql`,
  `docs/schema.dbml`, runbook, Mermaid view) emit on explicit
  invocation. Authors run after schema changes; CI runs to verify
  no-drift between source attributes and committed artifacts.

### What goes away
- Mermaid parser (`MermaidParser.cs`, `MermaidSchemaModel`) — no
  longer the input source.
- `AggregateEmitter` (Phase B) — survives only if we keep generating
  the slice/master Mermaid views; the input flips from
  `IEnumerable<MermaidSchemaModel>` to a Roslyn `ITypeSymbol` walk.
- `@surreal.*` Mermaid annotation directives — all replaced by typed
  attributes.

### What carries forward
- `.surql` emitter shape — the output format is the same; only the
  model feeding it changes. The byte-equivalence test (emit `.surql`
  from C# attributes, diff against the current Mermaid-generated
  `.surql`) is the migration acceptance gate.
- The partial-class convention (`Persistence/SurrealDb/CONVENTION.md`)
  — entities live in `Models/<Name>.cs` with helpers in sibling
  `Models/<Name>.<Aspect>.cs` files.
- The slice concept — moves from `[Slice("quest")]` attribute, same
  semantics.
- `Azoa.SurrealDb.Client` (runtime + typed queries) — untouched.
  POCOs the client consumes are still POCOs; only their authorship
  flips.

## File layout

```
Persistence/SurrealDb/
    Models/
        Quest.cs                  # attributes (hand-authored)
        Quest.Validation.cs       # OnValidating + domain helpers (hand-authored)
        Quest.g.cs                # generated — equality, copy ctors, JSON shape
        QuestNode.cs
        QuestNode.Validation.cs
        ... (one Models/<Name>.cs per entity, plus sibling Validation files)
        BridgeStatus.cs           # state-machine enum + Allowed transitions
    Guardrails/
        Guardrails.cs             # G1–G7 constant declarations
    Schemas/                       # generated .surql output (committed to git)
        NNN_<table>.surql
        ...
    CONVENTION.md                  # updated to reflect C#-first

docs/
    schema.dbml                    # generated DBML manifest (committed)
    RESIDUAL-RISK-RUNBOOK.generated.md
    aggregates/                    # optional Mermaid view, kept iff useful
        ...
```

The 24 `Persistence/SurrealDb/Schemas/source/*.mermaid` files are
deleted at the migration milestone.

## Migration path (no code this session — described only)

1. **Prototype slice — `wallet_nft` (3 entities, simple FKs)**.
   Author `Wallet`, `NftOwnership`, `SwapState` as decorated POCOs
   with partial-class validation. Build the source-gen inversion
   targeting just these three. Acceptance: emitted `.surql` for
   `010_wallet`, `030_swap_state`, `040_nft_ownership` is
   byte-identical to the Mermaid-generated versions.
2. **DBML emit for the prototype slice** + render check on
   dbdiagram.io.
3. **Migrate the bridge slice (5 entities)** as the second proof
   point — exercises state-machine codegen (BridgeStatus) + Guardrail
   attributes (G2 + G6 satisfied by IdempotencyKeyStore + SagaSteps).
4. **Migrate the remaining 16 entities** mechanically once the shape
   is proven.
5. **Retire the Mermaid sources + Phase B emitter** OR keep the
   Mermaid view as a downstream generator output if it still earns
   its place. Decide after step 3.
6. **CONVENTION.md rewrite** to reflect the C#-first authoring model.

## Open questions (resolve before prototype)
1. **Attribute namespace.** `AZOA.WebAPI.Persistence.SurrealDb.Schema.Attributes`
   vs `Azoa.SurrealDb.Schema.Attributes` (in the package). The
   second is cleaner if attributes are reused by the source-gen; the
   first keeps the surface AZOA-internal. Default: package
   namespace, since the source-gen already lives there.
2. **Validation framework — FluentValidation or homebake.** The
   prior `FluentValidation.AspNetCore` dependency is already in the
   project. Reusing it for the `OnValidating` body keeps the
   dependency surface flat. Default: FluentValidation.
3. **Cardinality inference vs explicit declaration.** The
   `[Relation(inverseOf: ...)]` shape above infers 1:M from the
   inverse field's nullability. Many-to-many via RELATE-edges
   (`forked_from`, `executes`) needs an explicit
   `[RelateEdge<From, To>(EdgeTable = "forked_from")]` attribute since
   the C# side has no natural inverse. Spec it out before the bridge
   slice prototype.
4. **HNSW + SurrealDB-specific shapes.** `EmbeddingVector(Dimension =
   384)` attribute is straightforward; less clear how to express the
   HNSW index declaration (DIMENSION + DIST COSINE). Likely a
   dedicated `[HnswIndex(Dimension = 384, Distance = "COSINE")]`
   attribute that emits the `DEFINE INDEX hnsw_<name>` block.
5. **Migration tooling.** Author a one-shot tool that reads the
   existing `.mermaid` files + emits the equivalent C# POCO + attribute
   scaffolding, to bootstrap the migration mechanically rather than
   by hand-typing 24 entities. Pays for itself once 5+ entities are
   migrated; cost less than 10 entities of hand-typing.

## Non-goals
- Public packaging / cross-ecosystem support. Scope is AZOA-internal.
  Other ecosystems can build their own; we're not the toolkit author.
- Replacing the validation pipeline — FluentValidation stays; we just
  collapse the model + validators into one place.
- Replacing `Azoa.SurrealDb.Client` (runtime, typed queries). The
  generated POCOs flow through it unchanged.
- Bespoke schema DSL. We're using C# *as* the DSL.

## What this commit lands
This design doc + the RUNBOOK update reflecting the new direction.
No code. No file moves. No deletions of the Mermaid sources or
Phase B emitter — those stay in tree until the prototype slice
proves the C#-first model works.

The track files for `surrealql-toolkit`, `surrealql-drift-detection`,
`surrealql-db-pull`, `surrealql-studio`, and
`surrealql-toolkit-packaging` are *kept in tree but stale* — they
describe the public-product direction that's no longer in scope.
Decide whether to delete them or annotate as deferred in the next
session, not tonight.

## References
- [RUNBOOK §4](../../../RUNBOOK.md) — phase plan; Phase C now reflects
  the C#-first redesign.
- [Persistence/SurrealDb/CONVENTION.md](../../../Persistence/SurrealDb/CONVENTION.md)
  — partial-class extension pattern; carries forward unchanged.
- [DBML spec](https://dbml.dbdiagram.io/docs/) — the diff/diagram
  target.
- [EF Core code-first](https://learn.microsoft.com/en-us/ef/core/modeling/)
  — closest .NET prior art for attribute-driven schema; we're not
  using EF Core but the attribute shape is directly comparable.
