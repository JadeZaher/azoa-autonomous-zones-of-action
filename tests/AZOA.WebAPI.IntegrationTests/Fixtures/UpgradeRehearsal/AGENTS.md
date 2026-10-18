# P2 rehearsal — sandbox data compatibility (round 4, owner decision D-9)

D-9 moves the schema by **generation reset**: a fresh database (`dev-up -ResetDb`; the Railway SurrealDB
volume is reset before the next deploy) instead of upgrading old rows in place. So P2 no longer replays
`main`'s rows through an upgrade; it rehearses that the **generated schema, applied by the real deploy path,
accepts and round-trips data shaped by the current models**.

`rehearse.ps1` (sweep step P2; PowerShell 7 or 5.1, podman by default, `AZOA_CONTAINER_RUNTIME=docker` works):

1. throwaway `surrealdb:v3.1.4` on RocksDB with `--user 0:0`, loopback port 8041–8049, container
   `azoa-p2-rehearsal` (refuses to reuse one);
2. composes `Generated/Schemas` + `CompatibilityBaselines` exactly like `docker-entrypoint.sh` and applies them
   with `surrealforge up --no-reconcile` plus `Migrations/`; a second `up` must report `applied 0 … evolved 0`;
3. runs `Round4Gates/DataCompatibility/GeneratedSchemaDataCompatibilityTests` against that sandbox
   (`AZOA_REHEARSAL_ENDPOINT/NAMESPACE/DATABASE`);
4. always removes the container and the temp schema directory.

The test class also runs in the ordinary integration job (no env vars): it bootstraps every golden into a
per-class namespace instead. For every POCO table it writes one **model-valid** row — values derived from the
column attributes (`[Inside]` first member, `[References]` → `table:<hex>`, the `[Assert]` regex families,
`[Required(NotEmpty)]` → non-empty, optional columns left NONE) — reads it back by key and re-writes it with a
typed `Upsert`; and it checks every declared `[Index(Unique = true)]` exists in `INFO FOR TABLE`. A table that
rejects a model-valid row is a generator/model mismatch (an ASSERT, DEFAULT or type the POCO does not satisfy):
exactly the class that would otherwise surface as a failed write after a side effect.

Not covered (and why): data written by older code is gone by design under D-9; ASSERT *negatives* (rows the
schema must refuse) belong to the store fixtures that own each invariant.
