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

## user.surql (2026-10-18, user-discoverable)

`user` was not previously baselined — it had no post-generation-reset field changes until this one.
`user.surql` here is the exact pre-change generated golden (no `discoverable` field); the forward
migration `../Migrations/20261018_090000__add_user_discoverable.surql` adds `discoverable` via
`DEFINE FIELD OVERWRITE` plus a one-shot NONE backfill. Pinned by
`SurrealDeploymentLeastPrivilegeContractTests.CompatibilityBaseline_RetainsTheShippedChecksum`
(SHA-256 `c29e9122d4888795249030376909e7ab0702d2a138030bca3a5fae468312af87`) alongside the five
tables baselined at the round-4 W1 re-base.

## Operator-actor audits (2026-10-18, node-admin-rbac-cli lane A2)

`kyc_control_audit`, `node_governance_audit`, `node_fee_audit`, `node_treasury_audit` and `action_recovery_audit`
gained an optional `actor` field (`legacy-operator` | `admin:<id>` | `root:<fingerprint>`). Each file here is the exact
pre-change generated golden; `../Migrations/20261018_120000__add_operator_actor_to_domain_audits.surql` adds the field
with `DEFINE FIELD OVERWRITE … TYPE option<string>`. Pinned by
`SurrealDeploymentLeastPrivilegeContractTests.CompatibilityBaseline_RetainsTheShippedChecksum`.

| File | SHA-256 |
|---|---|
| `kyc_control_audit.surql` | `d51c8459601a8afae92b9b6d5ed6c414881ee504100fdbe49f9a28d99f238aa9` |
| `node_governance_audit.surql` | `b2aa8a48bc44ec1fa86d26c795e7128a34abca7108966282ae00912308fe9cfe` |
| `node_fee_audit.surql` | `24c2f0854f76d024477b412768ca7cd90ff73e1678095a3cf60e2fc8e85c2913` |
| `node_treasury_audit.surql` | `ca09cda1ee4c9dca9fd3477a936c719ec7e568c5b671be920dedc65dac69e9da` |
| `action_recovery_audit.surql` | `2c338a817a619e3b04255c3b23d75a2b0331fdf2bb1027e787f97517e9e632bb` |
