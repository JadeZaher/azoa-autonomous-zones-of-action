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

## Recovered July 18 baselines

The contract tests pin every compatibility file by SHA-256. These three were
recovered byte-for-byte from Git after the production ledger reported their
prior checksums; their paired forward migrations are `135000` and `136000`.

| File | Source commit | SHA-256 |
|---|---|---|
| `holon.surql` | `8102f59bfbeaa8aa25ee09161d9c887547bd556a` | `befd3f9dd6e9a15a8f6dcc278153d028c1da1629b836bf2f15621424bfd7605e` |
| `operation_log.surql` | `9220b3ff8e425b7d7a69fe542e5a9456bcfcbf08` | `f67cb9da11f239416dabb42ab9839f9615bb782aa586688d6ce95c8da7b8994c` |
| `swap_state.surql` | `607303d884c6eaa2c15dd716f39cd73a1ce952f3` | `40f8f1ba577fbb6511da0432c3a0f2616e8b28e35e0477d7bc69a0808ea2ef99` |
