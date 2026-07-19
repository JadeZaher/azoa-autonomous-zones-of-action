# Deploying an Azoa node on Railway

An Azoa node is three long-running services plus one one-shot release job:

| Service | What it is | Source |
|---|---|---|
| **SurrealDB** | Sole storage engine: v3.1.4, RocksDB at `/data/db`, synchronous writes | Pinned `docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60` multi-arch image |
| **azoa-schema** | One-shot schema and runtime-user provisioner | Same digest-pinned, attested image as `azoa-api` |
| **azoa-api** | .NET 10 WebAPI backend | Verified GHCR image, promoted from this repo |
| **azoa-frontend** | Next.js 16 dashboard | Verified GHCR image, promoted from this repo |

The frontend is pinned to Next.js `16.2.10`. Its lockfile excludes the unused
Solana SDK dependency chain and is built with `npm ci`; the release gate requires
`npm audit` to report zero known vulnerabilities. Do not use
`npm audit fix --force` as a release shortcut.

The exact release contract is in [`template.json`](./template.json). CI validates
that contract with [`render-template.py`](./render-template.py); the protected
promotion workflow publishes a fully materialized blueprint artifact containing
whole `name@sha256` image references.

## Required release order

Deploy one release serially. Do not start all four services together:

1. Start the pinned `surrealdb` service and require `/health` to pass.
2. Deploy `azoa-schema` from the promoted API digest and require terminal
   `SUCCESS`. A checksum mismatch or failed migration stops the release.
3. Deploy `azoa-api` from that exact same digest and require `/health` to pass.
4. Deploy `azoa-frontend` from the matching promotion and require
   `/api/health` to pass.

Never promote the API before its schema job succeeds. Rollback means restoring
the prior API and frontend digests; schema changes are forward-only.

## Controlled serial promotion

The blueprint provisions a fail-closed service graph; it is not evidence that a
release was promoted in order. The committed placeholders prevent API/schema and
frontend deployment, and a materialized blueprint only supplies reviewed sources
and configuration. Production promotion is GitHub Actions only: the protected
workflow builds and attests the images, then runs
[`serial-rollout.py`](./serial-rollout.py) with Railway CLI 5.27 or newer. Do not
use `railway link`; the helper requires explicit UUIDs for the project,
environment, and every service. Railway CLI 5.27 added the project selector to
`deployment list`, so every lookup and image-config edit carries explicit scope
and never inherits a linked project.

Choose the SurrealDB gate deliberately:

- `--surreal-mode redeploy` applies the explicit pinned `--surreal-image` and
  waits for its new deployment to reach terminal `SUCCESS`. Because the service config owns
  the `/health` healthcheck, `SUCCESS` is also Railway's health-gate result.
- `--surreal-mode already-healthy` is for a preserved database deployment.
  Before running it, inspect the exact project/environment/service in Railway
  and confirm `/health` is green. The helper additionally requires the latest
  SurrealDB deployment to be `SUCCESS`; it does not expose SurrealDB publicly.

Example (replace every value; the confirmation string is intentionally exact):

```bash
PROJECT_ID=<project-uuid>
ENVIRONMENT_ID=<environment-uuid>
python3 deploy/railway/serial-rollout.py \
  --project-id "$PROJECT_ID" \
  --environment-id "$ENVIRONMENT_ID" \
  --surreal-service-id <surreal-service-uuid> \
  --schema-service-id <schema-service-uuid> \
  --api-service-id <api-service-uuid> \
  --frontend-service-id <frontend-service-uuid> \
  --surreal-image docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60 \
  --api-image ghcr.io/<owner>/<api>@sha256:<64-hex-digest> \
  --frontend-image ghcr.io/<owner>/<frontend>@sha256:<64-hex-digest> \
  --surreal-mode already-healthy \
  --api-health-url https://<api-domain>/health \
  --frontend-health-url https://<frontend-domain>/api/health \
  --confirm "PROMOTE ${PROJECT_ID}/${ENVIRONMENT_ID}"
```

The helper writes each exact `name@sha256` reference with
`railway environment edit ... source.image`, never uploads or rebuilds source.
It snapshots deployment IDs, rejects concurrent/ambiguous deployments, verifies
the terminal deployment's `meta.imageDigest`, reads the service image back for
drift, and advances only after these gates:
SurrealDB health, schema terminal `SUCCESS`, API terminal `SUCCESS` plus public
`/health`, then frontend terminal `SUCCESS` plus public `/api/health`. Unknown or
failed states, malformed CLI JSON, redirects, non-2xx probes, oversized or
non-Azoa health JSON, and bounded timeouts stop the rollout; later services remain untouched. The helper never
rolls schema backward and never prints response bodies, URLs, or Railway stderr.

## Storage invariants

The SurrealDB service is deliberately not a floating `3.x` template. Keep all
four controls together: image
`docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60`,
command `/surreal start` with the committed `SURREAL_*` variables, volume mount
`/data`, and
`SURREAL_SYNC_DATA=true`. Changing any one is a storage migration and requires a
separate restore drill.

The API persists ASP.NET Data Protection keys at
`/app/data/data-protection-keys` on its `/app/data` volume. The template pins one
API replica because a Railway volume is attached to one service instance. Do
not scale replicas until they share an external Data Protection key repository
and the same `DataProtection__ApplicationName`.

## Blockchain honesty posture

Out of the box **all real-value bridge execution is disabled**
(`Blockchain__Bridge__RealValueEnabled=false`). No provider currently exposes a
complete reviewed lock/mint/burn/release plus production-custody lifecycle, so
there is no launchable real-value route. `/health` and provider capability gates
fail closed if the switch is enabled prematurely. Wormhole is additionally
hard-blocked until real sequence derivation and end-to-end execution land.
Set `Blockchain:Mode=Simulated` only in dev/test for deterministic `sim:tx:`
settlement. See `docs/NODE-HOST.md` §8 for the go-to-production checklist.

## Isolated local schema QA volume

Schema checksum drift remains fail-closed: the launchers never add
`surrealforge --force` and never wipe the ordinary development volume. To test
the current schema against a clean, separate local volume, select an explicit
engine-level name before launching:

```powershell
$env:AZOA_SURREAL_VOLUME_NAME = 'azoa-schema-qa-20260718'
.\dev-up.ps1
```

```bash
AZOA_SURREAL_VOLUME_NAME=azoa-schema-qa-20260718 ./dev-up.sh
```

The launchers merge `docker-compose.qa-volume.yml`, so only the SurrealDB data
volume changes. Unset the variable to return to the preserved ordinary volume.
`-ResetDb`/`--reset-db` deletes whichever explicitly selected volume is active;
never use a production or irreplaceable volume name here.

Ordinary local schema application is owned by the API container entrypoint for
both bundled and host-external SurrealDB. The NuGet package is a framework-
dependent CLI payload, not a restorable .NET tool, so the host launchers do not
run `dotnet tool restore`. Explicit `-Reset`/`--reset` invokes the same packaged
CLI inside `azoa-dev-api`; checksum drift remains an error on the ordinary `up`
path and is never silently forced.

## Operator-gated template

[`template.json`](./template.json) contains whole-image placeholders on purpose.
It must not be published or imported as committed. Blueprint provisioning is
therefore fail-closed, but it does not serialize a controlled promotion. Run
the protected promotion
workflow and download its `azoa-railway-template-<sha>` artifact; it replaces
both `<PROMOTED_*_IMAGE_REFERENCE>` values with attested immutable
`ghcr.io/...@sha256:...` references and validates the rest of the release
contract. There are no branch-connected production services.

For an offline review, validate the committed gate or materialize explicit
promoted references:

```bash
python3 deploy/railway/render-template.py --check
python3 deploy/railway/render-template.py \
  --api-image ghcr.io/example/azoa@sha256:<64-hex-digest> \
  --frontend-image ghcr.io/example/azoa-frontend@sha256:<64-hex-digest> \
  --output /tmp/azoa-railway-template.json
```

The API is configured with `AZOA_SKIP_MIGRATIONS=1` and receives only the
database-scoped runtime account. The schema job alone receives the SurrealDB
owner credentials. It applies committed schema, then creates or rotates
`azoa_runtime` as a database-level `EDITOR`; that built-in role can read and
write database resources but cannot manage users or tokens. SurrealDB's built-in
role is not DDL-proof; the isolation guarantee here is removal of owner/IAM
authority. A fresh database, a schema change, or a runtime-password rotation
requires a successful schema job before API rollout.

After materializing the digest-pinned blueprint and completing the controlled
serial promotion above, publish it with
Railway's dashboard **Create Template from Project** on a deployed Azoa project —
`template.json` here is the source-of-truth blueprint for that step (service
names, sources, and the `${{service.VAR}}` references must match).

## Manual stand-up (what the automation does, by hand)

1. **SurrealDB** — create the service from
   `docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60`.
   Copy the blueprint's exact `/surreal start` command,
   `/data` volume, `SURREAL_SYNC_DATA=true`, credentials, private-URL variable,
   `/health` check, and `RAILWAY_RUN_UID=0` required by this pinned upstream
   image/volume shape. The generated credentials are owner credentials:
   reference them only from `azoa-schema`, never from `azoa-api`.

2. **Promote images** — configure the protected `railway-production` GitHub
   environment with `RAILWAY_TOKEN` plus the project, environment, four service
   IDs, and two health URLs named by the workflow. Then run
   `Promote attested conformance image` with the exact current `main` SHA and its
   successful CI run id. It verifies the CI artifact, builds and attests both
   images, and promotes only those immutable digests in dependency order. Local
   uploads, FTP, `railway up`, and source redeploys are outside production.

3. **azoa-schema** — create the one-shot service from the promoted API digest.
   Configure its custom start command as
   `/usr/local/bin/docker-entrypoint.sh schema`, give it the `SURREALFORGE_*`
   owner references shown in the blueprint, and use `ON_FAILURE` with one
   bounded retry so a nonzero exit remains visible.
   The image's non-root runtime applies schema, provisions the database runtime
   user, and exits without ever starting the WebAPI host.
   Deploy it and require terminal `SUCCESS` plus the explicit `Schema job
   completed successfully` log marker before continuing. Failure or schema
   checksum drift blocks the release; never add `--force`.

4. **azoa-api** — create the service from the exact same promoted API digest.
   The production backend reads the isolated `SurrealRuntime__*` env family
   (see `docs/NODE-HOST.md`). Its user and password reference
   `azoa-schema.AZOA_RUNTIME_*`; neither `SURREALFORGE_*` nor SurrealDB owner
   credentials belong on this service. Set
   `SurrealRuntime__AuthenticationScope=Database`; this makes the client send
   SurrealDB 3's `Surreal-Auth-NS` and `Surreal-Auth-DB` headers for the
   database-scoped Basic identity. The promoted image embeds bounded
   conformance evidence read-only at `/app/conformance`.
   Required secrets: strong random `Jwt__Key`, `AZOA__WalletEncryptionKey`, and
   `NodeOperator__Password`. Never commit them or bake them into an image.
   Seed the reserved node authority with `NodeOperator__Username=node-operator`,
   `NodeOperator__CredentialRevision=1`, and
   `NodeOperator__SessionMinutes=20`. The password must be a generated 24–72
   byte value known to the node owner; only its bcrypt hash is persisted.
   Required non-secret:
   `Cors__AllowedOrigins__0` = the frontend's public URL (the app fail-closes at
   boot if CORS origins are empty in Production). Preserve the `/app/data`
   volume and `DataProtection__KeyRingPath`. Set `RAILWAY_RUN_UID=0`: the
   entrypoint accepts root only long enough to constrain the key-ring path below
   `/app/data`, repair the mounted volume to `APP_UID=1654`, and then uses the
   image-bundled `setpriv` to drop both UID and GID before `dotnet` starts. The
   WebAPI therefore does not run as root. Generate a public domain.

5. **azoa-frontend** — create from the promoted frontend digest emitted by the
   same workflow run. Set `API_URL` to the
   backend's **public** domain (resolved at request time into
   `window.__RUNTIME_CONFIG__`, so the browser calls the right host). Generate a
   public domain. Production rejects a missing, non-HTTPS, localhost, private-IP,
   or `.internal` API URL. `/api/health` performs a three-second, no-store probe
   of the resolved API `/health`; invalid config, timeout, redirect, network
   failure, or non-2xx returns a generic 503 without leaking the upstream URL,
   status, body, or exception.
   `AZOA_ALLOW_INSECURE_LOCAL_API=true` exists only for the local compose stack
   and must never be configured on Railway.

6. **Operator and KYC control plane** — open the frontend at
   `/operator/login` with the seeded credentials. Verify persistence on the
   overview before configuring tenants. KYC starts as
   `Kyc__Provider=unavailable`; the blueprint exposes blank host-managed slots
   for Veriff and generic-hosted metadata/secrets without fabricating working
   credentials. Add the chosen provider values to the API service, redeploy,
   then use `/operator/providers` to inspect required/missing key names and
   secret-free readiness. External adapters remain unavailable scaffolds in
   this release even when their slots are complete. A tenant can select only a
   profile the API evaluates as `READY`.

   Normal **End operator session** clears one browser cookie. Use the separately
   confirmed **Revoke all operator sessions** action only for an incident or
   handoff. Rotate credentials by changing username/password and incrementing
   `NodeOperator__CredentialRevision`; lower or reused revisions with different
   credentials refuse startup.

## KYC schema rollout

`kyc_submission.surql` shipped before provider-neutral hosted verification was
added. SurrealForge correctly refuses an in-place checksum change, so the
container schema job overlays the exact v1 bytes from
`Persistence/SurrealDb/CompatibilityBaselines/` and then applies
`20260718_120000__extend_kyc_provider_contract_v2.surql`. That migration expands
the provider enum, adds hosted-session metadata, backfills existing manual and
Veriff rows, and only then makes the new capability fields required. Do not
delete the baseline, edit the timestamped migration, or add `--force`.

## Gotchas learned standing this up

### Conformance promotion

`Dockerfile.conformance-release` exists only for the promotion workflow. It
adds the verified evidence bundle as root-owned read-only files and gives the
runtime account write access only to `/app/data`. The same workflow builds the
frontend from its lockfile. Do not use either image from a local or unverified
build. Before enabling the `conformance-promotion` GitHub
environment, configure required reviewers there; the workflow's environment
name alone cannot provide approval protection. Railway must be updated only to
the emitted image digests, with the API's `/app/data` volume preserved and the
conformance runtime configuration set to the same repository, CI workflow, and
source revision. Before any such update, verify both published image
attestations against this repository and the protected promotion workflow.

- **One `railway.json` per repo applies to every service.** Keep Dockerfile
  selection in each service's `RAILWAY_DOCKERFILE_PATH` variable, not in a
  pinned `build.dockerfilePath` in the root config — otherwise every service
  builds the same Dockerfile.
- **No `startCommand` override for the API.** The root config must NOT set
  `startCommand` — the backend's `docker-entrypoint.sh` lives at
  `/usr/local/bin/` and is the Dockerfile `ENTRYPOINT`; a `./docker-entrypoint.sh`
  startCommand (relative to WORKDIR `/app`) silently exits. The one-shot schema
  service is the deliberate exception and uses the absolute command shown above.
- **Root is a volume-repair bootstrap, not the API identity.** Keep
  `RAILWAY_RUN_UID=0`, `util-linux`/`setpriv`, the `/app/data` path constraint,
  and the `APP_UID=1654` drop together. Removing only one of these either breaks
  a fresh Railway volume or invalidates the non-root runtime guarantee.
- **The frontend layout must be `force-dynamic`** so `API_URL` is read per
  request, not baked at build time.
- **The frontend release gate is mandatory.** CI runs `npm ci`, a zero-known-
  vulnerability audit, lint, typecheck, and production build before a promotion
  can consume the successful CI run.
- **CORS is required in Production.** Set at least `Cors__AllowedOrigins__0`.
