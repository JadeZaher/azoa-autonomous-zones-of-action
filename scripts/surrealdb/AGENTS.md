# scripts/surrealdb -- backup/restore

## Why exec-based, not host-side `surreal` CLI

`surreal export`/`surreal import` run **inside** the SurrealDB container against
its own loopback endpoint (`http://localhost:8000` from the container's point
of view), not from the host. The container image ships no shell (`sh`/`ls`
are absent) and is not expected to have a bind-mounted host filesystem for
`.surql` files, so the scripts move files across the container boundary with
`<runtime> cp` rather than a shared volume:

1. `backup.ps1`: `<runtime> exec <container> /surreal export ... /tmp/<uuid>.surql`
   then `<runtime> cp <container>:/tmp/<uuid>.surql <OutputPath>`.
2. `restore.ps1`: `<runtime> cp <InputPath> <container>:/tmp/<uuid>.surql` then
   `<runtime> exec <container> /surreal import ... /tmp/<uuid>.surql`.

The container-local temp file is left in `/tmp` (best-effort; the image has no
`rm`-capable shell to invoke via `exec`) -- harmless since `/tmp` is not on the
persisted `surrealdb_data` volume and is cleared on container recreation.

## Runtime auto-detect

`ContainerRuntime.ps1` exports `Find-ContainerRuntime`, mirroring the
`Find-Compose` idiom in `dev-down.ps1` at the repo root: try `docker` first,
fall back to `podman`, throw if neither responds to `<runtime> version`. Both
scripts dot-source it and log the resolved runtime on startup.

## `surreal export` does not include `DEFINE NAMESPACE`/`DEFINE DATABASE`

Confirmed empirically (SurrealDB v3.1.4): `surreal export --namespace X
--database Y` exports only the tables/data *within* that NS/DB context, not
the NS/DB definitions themselves. `surreal import` against a namespace that
was `REMOVE NAMESPACE`'d recreates the NS/DB implicitly (root-authenticated),
but callers that immediately re-query over HTTP `/sql` using only the
`NS`/`DB` request headers (no `USE` statement in the request body) can hit a
transient "Specify a namespace to use" error on the very next request after a
fresh restore. `G5_RestoreDrillTest.cs` defensively re-issues `DEFINE
NAMESPACE IF NOT EXISTS` + `USE NS ... DB ...; DEFINE DATABASE IF NOT EXISTS`
right after calling `restore.ps1`, before querying -- any other restore
consumer (operator runbook, future gate) should do the same.

## Container name default

Both scripts default `-ContainerName` to `azoa-dev-surrealdb` (the
`docker-compose.dev.yml` service/container name). Override with
`-ContainerName` if driving against a differently-named container (e.g. a
CI-spun container with another name).

## Manual verification (2026-07-05)

Round-tripped a throwaway namespace end-to-end against the live
`azoa-dev-surrealdb` podman container: seed row -> `backup.ps1` -> `REMOVE
NAMESPACE` -> `restore.ps1 -Force` -> re-queried row byte-identical. Confirms
the scripts work against the real container without needing the G5 xUnit
harness (which requires `pwsh.exe`, not installed on this dev machine --
Windows PowerShell 5.1 is present and is what these scripts target/were
verified against; they avoid PS7-only syntax so they work under either).
