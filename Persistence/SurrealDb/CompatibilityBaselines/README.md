# Immutable schema compatibility baselines

SurrealForge records each generated schema filename and SHA-256 in its
`schema_migration` ledger. Once a generated file has shipped, changing that file
in place would make `surrealforge up` stop on checksum drift before a forward
migration could run.

The container entrypoint therefore materializes its runtime schema directory by
copying `Generated/Schemas/` and then overlaying files from this directory. An
overlay must retain the exact shipped filename and bytes. A timestamped
forward-only migration under `../Migrations/` evolves that baseline to the
current generated schema. Fresh databases follow the same baseline-plus-forward
path as upgraded databases.

Do not edit or remove an applied baseline. Add a new migration for every later
change. The generated file remains the current desired-schema golden used by
the POCO equivalence tests; the compatibility baseline exists only for the
checksum-safe deployment path.
