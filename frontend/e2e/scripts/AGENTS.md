# E2E stack ownership

The project name is bounded and defaults to the original CI project. Custom local projects
have separate generated credentials, certificates, containers, networks and volumes.
Before any reuse or removal, inspect every selected resource and require its owner label
to match this run's marker. An absent marker never authorizes adopting an existing stack.
Generated trust/probe resets must resolve inside the selected generated directory.

Inspect container metadata, published ports and network CIDRs before launching a local
project. Never run `down` against an unknown or unlabelled stack. The main workflow and
production node are outside this helper's ownership.

## Build provider verification

Before any build, the selected Compose provider must return `config --format json`. Its
secret-bearing output stays in process memory and is never printed or written. The guard
requires the exact project, four services, per-run owner/container names, reviewed API and
frontend Dockerfile paths, repository contexts and API Git revision. Only a sanitized proof
is persisted. The Git subprocess must succeed and return an exact lowercase 40-character
HEAD, which overrides any inherited `SOURCE_REVISION`. Unsupported providers fail closed:
the installed Python podman-compose on
Windows classifies drive-letter contexts as URLs and silently drops custom Dockerfiles.
Use a verified Compose v2 provider instead of accepting its builds. For a machine whose
default OpenSSL points to a missing configuration, a process-only PATH/OPENSSL_CONF override
may select an installed matching Git for Windows OpenSSL and config; no system setting changes.

## Checked-out database contract

The native Node launcher reads deploy/railway/surreal-contract.json relative to
this exact checkout, with no Python dependency or copied image digest. Its strict
key sets and supported3.2.0 schema match the Python loader, rejecting extra fields
or a silently changed version. It requires an immutable official image, amd64 platform
digest and SurrealKV /data/db with explicit sync=every. Missing/unsupported input
fails closed; no old image or RocksDB fallback exists in this isolated stack.
Contract-derived image, URI, version and receipt hash are spread after generated
and inherited settings, so environment overrides cannot silently select another
engine. Other settings retain their existing process-over-generated precedence.

Resolved Compose JSON must show the contract image, linux/amd64 and exact storage
command before any build. Only those public facts and the contract hash join the
sanitized build proof. After up, the actual owned database container's public
version read has a15-second/4KiB bound and must match the contract before health waits and journeys proceed. The
named test volume stays owned by this isolated project; no development volume,
legacy dev engine or production datastore is touched or converted here.

## Existing database volume compatibility

New isolated database volumes carry the exact canonical contract-file SHA256 in
com.azoa.e2e.surreal-contract. Before prepare/up can build or launch, the launcher
loads that hash before resource inspection and requires a matching label on any
existing owned database volume, after the unchanged owner guard. Missing/mismatched
labels refuse: no retagging, migration, implicit wipe or engine conversion occurs.
Resolved Compose must also declare the label before a new volume can be created.

The pure database-volume-contract helper is the actual admission check, not a
test-only replica. Its built-in Node tests cover current/missing/mismatched labels
and cleanup ownership with no process, engine or filesystem fixture. Owner-scoped
down --wipe can remove an obsolete volume despite its old/missing contract label;
neither down nor logs bypasses ownership. Legacy dev/rehearsal volumes remain
outside this project's resource set. Run the focused suite only at the agreed
final validation gate: node --test e2e/scripts/database-volume-contract.test.mjs
from frontend.
