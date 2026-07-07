# Deploying an Azoa node on Railway

An Azoa node is three Railway services:

| Service | What it is | Source |
|---|---|---|
| **SurrealDB** | Sole storage engine (v3.x) with a persistent `/data` volume | Public template [`surrealdb-3x`](https://railway.com/deploy/surrealdb-3x) |
| **azoa-api** | .NET 10 WebAPI backend | This repo, root `Dockerfile` |
| **azoa-frontend** | Next.js 14 dashboard (bundles the `@azoa/sdk` build in its Dockerfile) | This repo, `frontend/Dockerfile` |

The exact, production-validated wiring is in [`template.json`](./template.json).

## Blockchain honesty posture

Out of the box a node runs **Algorand real bridge value LIVE**;
Solana / Wormhole / Ethereum value routes are **fail-closed and disabled**
(`RealValueEnabled=false`). Set `Blockchain:Mode=Simulated` for deterministic
`sim:tx:` settlement in dev/test. See `docs/NODE-HOST.md` §8 for the full
go-to-production checklist (guardian sets, admin bootstrap, mainnet gate).

## One-click template

The published Railway template stands all three services up with the variable
references already wired. To (re)publish it from a working project, use
Railway's dashboard **Create Template from Project** on a deployed Azoa project —
`template.json` here is the source-of-truth blueprint for that step (service
names, sources, and the `${{service.VAR}}` references must match).

## Manual stand-up (what the automation does, by hand)

1. **SurrealDB** — deploy the `surrealdb-3x` template into your project:
   ```
   railway deploy -t surrealdb-3x
   ```
   It creates a service exposing `SURREAL_USER`, `SURREAL_PASS`, and
   `SURREAL_HTTP_PRIVATE_URL`, with a `/data` volume.

2. **azoa-api** — create the service from this repo and wire it to SurrealDB.
   The backend reads the `SurrealDb__*` env family (see `docs/NODE-HOST.md`);
   reference the SurrealDB service so creds track automatically:
   ```
   railway add --service azoa-api \
     --variables "SurrealDb__Endpoint=${SurrealDb__Endpoint}" ...
   railway service source connect --repo <owner>/<repo> --branch main --service azoa-api
   ```
   Required secrets (generate strong random values):
   `Jwt__Key`, `AZOA__WalletEncryptionKey`. Required non-secret:
   `Cors__AllowedOrigins__0` = the frontend's public URL (the app fail-closes at
   boot if CORS origins are empty in Production). Generate a public domain.

3. **azoa-frontend** — create from this repo with `frontend/Dockerfile`; its build
   compiles the SDK (`npx tsup`) then the Next.js app. Set `API_URL` to the
   backend's **public** domain (resolved at request time into
   `window.__RUNTIME_CONFIG__`, so the browser calls the right host). Generate a
   public domain.

## Gotchas learned standing this up

- **One `railway.json` per repo applies to every service.** Keep Dockerfile
  selection in each service's `RAILWAY_DOCKERFILE_PATH` variable, not in a
  pinned `build.dockerfilePath` in the root config — otherwise every service
  builds the same Dockerfile.
- **No `startCommand` override for the backend.** The root config must NOT set
  `startCommand` — the backend's `docker-entrypoint.sh` lives at
  `/usr/local/bin/` and is the Dockerfile `ENTRYPOINT`; a `./docker-entrypoint.sh`
  startCommand (relative to WORKDIR `/app`) silently exits.
- **The frontend layout must be `force-dynamic`** so `API_URL` is read per
  request, not baked at build time.
- **CORS is required in Production.** Set at least `Cors__AllowedOrigins__0`.
