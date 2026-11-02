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
