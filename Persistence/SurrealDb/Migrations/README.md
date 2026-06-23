# Hand-authored SurrealDB migrations

This directory holds **hand-authored** `.surql` files that aren't part of
the schema (which lives in [`../Generated/Schemas/`](../Generated/Schemas/)
and is auto-generated from `[SurrealTable]` POCOs).

Typical reasons to author a migration here:

- **Data backfills** — populate a new column on existing rows
- **Rewrites** — change the wire format of an existing column on existing rows
- **One-shot fixes** — patch a bad row produced by a prior bug
- **Index-only changes** that need to land independently of the schema rebuild
- **Seed data** — dev / test fixtures (e.g. an admin avatar in a fresh namespace)

## Naming convention

```
YYYYMMDD_HHMMSS__<short_description>.surql
```

Examples:

```
20260605_120000__backfill_avatar_records_with_default_karma.surql
20260606_093000__seed_dev_admin_avatar.surql
```

The timestamp prefix is lexically sortable; the `azoa-surreal up` runner
applies files in `StringComparer.Ordinal` order. Use the canonical `__`
(double-underscore) separator between timestamp and description so an
operator scanning `ls` output can read the description fluently.

## Apply order

```
azoa-surreal up
  -> Persistence/SurrealDb/Generated/Schemas/*.surql   (lexical)
  -> Persistence/SurrealDb/Migrations/*.surql          (lexical)
```

Schemas come first so any table / field / index a migration needs to
touch is guaranteed to exist. Both directories funnel through the same
`schema_migration` ledger (keyed by file name + SHA-256), so re-running
`azoa-surreal up` is a no-op when no files changed.

## Authoring rules

1. **Idempotent writes only.** Migrations get re-applied to fresh
   namespaces all the time. Wrap inserts in `UPSERT` or in a
   `IF NOT EXISTS` predicate. Never `DELETE FROM table` blindly.
2. **No DDL.** Schema DDL belongs in the POCO; the generator emits the
   `.surql`. If you need a new column, edit the POCO, not a migration.
3. **One purpose per file.** A migration that mixes a backfill with a
   seed-data row is harder to roll forward + harder to read in
   diff review.
4. **Comment the why.** Top-of-file `-- ...` lines should explain the
   non-obvious motivation (link the bug report, the prior incident, the
   spec section). Future-you will thank present-you.

## Status check

```
azoa-surreal migrate status
```

Lists every applied file with its checksum + apply timestamp. A file
appears only after it has been applied; the same file applied to two
different environments shows up once per environment-local ledger.

## Drift detection

If a migration file's content changes after it has been applied, the
runner detects the checksum drift and refuses to proceed with a
`MigrationChecksumMismatchException`. Two valid responses:

1. **Revert the edit** — restore the file to the previously-applied
   content. The original migration stays canonical.
2. **Force re-apply** — `azoa-surreal up --force` overwrites the
   recorded checksum. Use this only when the edit is intentional AND
   the new content is semantically idempotent (re-applying it produces
   the same final DB state).

Authoring a new migration to fix a prior one is almost always cleaner
than editing the prior file. The ledger is an append-only record of
what actually ran against the namespace.
