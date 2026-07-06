# Data-backfill primitive (`Services/Backfill/`)

The application-level **data**-migration primitive: an `IBackfill` unit, a
`BackfillRunner` that applies pending units once and records them in the
`data_migration` ledger, and an Operator-guarded controller
(`POST /api/admin/backfill/apply`, `GET /api/admin/backfill/list`).

Shipped as `final-hardening-cutover` Phase E2. Greenfield pre-launch: **zero
rows to migrate today** — this exists so no data-rewrite *path* is left unbuilt,
not to gold-plate a migration tool. The registry is intentionally empty; the
first consumer (Phase C/F6: FK `string` → `record<table>` rewrite) is another
worker's territory and is deliberately not written here.

## Why this exists alongside `schema_migration`

surrealforge already owns a `schema_migration` ledger (see
`Persistence/SurrealDb/CONVENTION.md` §10) — but that is for **DDL** (`.surql`
schema/index/param definitions applied by `surrealforge up`). This primitive is
for **data** rewrites expressed in C#: reshaping existing rows, backfilling a new
column from old ones, rewriting an FK's wire form. Two ledgers, two concerns; the
tables are deliberately distinct so a DDL apply and a data backfill never collide
on one bookkeeping key.

## The contract (why it is shaped this way)

- **Idempotent by construction.** A backfill is recorded in `data_migration`
  keyed on its `Id` (record id + UNIQUE column). The runner skips any id already
  present, so re-running `apply` is a no-op. This mirrors the insert-wins
  discipline of `consumed_vaa_ledger` / `idempotency_key_store`: a duplicate
  CREATE is rejected per-statement and read as "already applied", never an error.
- **`ApplyAsync` must itself be re-runnable.** The ledger prevents a *second*
  successful apply, but a crash *between* a successful rewrite and the ledger
  write means the next run re-invokes `ApplyAsync`. Units therefore MUST guard on
  target state (e.g. `WHERE old_shape` predicates), never assume a clean slate.
  The ledger is the fast-path skip, not the only correctness guard.
- **Ordered.** Units apply in ascending `Order`, then by `Id`. A failure halts
  the run (successors may depend on a predecessor's rewrite). Partial progress is
  durable — each successful unit is recorded before the next runs.
- **Ids are permanent.** `IBackfill.Id` is the dedup key. Never rename or reuse
  an id; a rename re-runs a "new" backfill against already-migrated data.
- **`Checksum` is advisory.** SHA-256 of the unit's logical body (`BackfillBase`
  derives it). It records drift for observability; the ledger keys on `Id`, so a
  changed checksum does NOT auto-re-apply — that would violate applied-once. If a
  shipped backfill's logic must change, ship a NEW unit with a new id.

## Authoring a backfill

```csharp
public sealed class RewriteFooFks : BackfillBase
{
    public override string Id => "2026-07-05-rewrite-foo-fks"; // permanent, unique
    public override string Name => "Rewrite foo.bar_id to record<bar> form";
    public override int Order => 100;
    protected override string ChecksumSource => Id + ":v1"; // bump when body changes → new id

    public override async Task<BackfillResult> ApplyAsync(BackfillContext ctx, CancellationToken ct)
    {
        // ctx.Executor is the same ISurrealExecutor the stores use.
        // Guard on the OLD shape so a re-run after a crash is safe.
        // ... run the rewrite, return rows affected ...
        return new BackfillResult(rowsAffected);
    }
}
```

Register it in `Program.cs` next to the backfill block:

```csharp
builder.Services.AddScoped<AZOA.WebAPI.Services.Backfill.IBackfill, RewriteFooFks>();
```

The runner discovers all registered units via `IEnumerable<IBackfill>`.

## Surface

| Verb | Route | Guard | Effect |
|---|---|---|---|
| `GET`  | `/api/admin/backfill/list`  | `Operator` policy | Registered units + applied/pending status, in apply order |
| `POST` | `/api/admin/backfill/apply` | `Operator` policy | Apply all pending units in order, record each; idempotent |

No CLI binary — the repo has no CLI project (only `AZOA.WebAPI` + 3 test
projects). The spec's `azoa-surreal backfill` CLI + `packages/Azoa.SurrealDb.Schema/`
paths are stale; the surface is this admin controller instead.

## Files

| File | Role |
|---|---|
| `IBackfill.cs` | Primitive interface + `BackfillContext` + `BackfillResult` |
| `BackfillBase.cs` | Abstract base: checksum + `Order` defaults |
| `BackfillRunner.cs` | List + apply-pending; ledger-checked, ordered |
| `../../Interfaces/Stores/IDataMigrationLedgerStore.cs` | Ledger boundary |
| `../../Providers/Stores/Surreal/SurrealDataMigrationLedgerStore.cs` | Ledger impl |
| `../../Persistence/SurrealDb/Models/DataMigration.cs` | `data_migration` POCO |
| `../../Controllers/BackfillController.cs` | Operator-guarded surface |

## Schema regen

`DataMigration.cs` follows the decorated-POCO convention. Its `.surql` golden
(`Persistence/SurrealDb/Generated/Schemas/data_migration.surql`) is
machine-generated: the `AttributePocoByteEquivalenceTests` gate auto-authors it
from the POCO when the committed file is absent/empty (`AZOA_REGENERATE_GOLDENS`
path). Never hand-edit it — edit the POCO and let the test regenerate.
