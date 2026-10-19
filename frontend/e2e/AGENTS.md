# e2e — Playwright journeys against a dedicated E2E stack

Real browser, real frontend and API containers, real SurrealDB. Only Identomat and Stripe are
fake, and they are fakes **the API reaches over HTTPS**, not mocks inside it. Journeys are
`journeys/*.journey.ts` (the repo `.gitignore` swallows `*.spec.ts`); `shell/*.shell.ts` needs no
API.

## §stack — bring-up

```powershell
cd frontend
node e2e/scripts/e2e-stack.mjs up      # certs, trust dir, secrets, egress probe, build, compose up, health waits
npm run test:e2e                        # global setup starts the fake vendors and proves hermeticity
node e2e/scripts/e2e-stack.mjs down    # stop the project; keeps its volume (add --wipe to drop it)
```

- `docker-compose.e2e.yml` is a **self-contained compose project, `azoa-e2e`**: its own SurrealDB
  (unpublished), container names `azoa-e2e-*`, volume `azoa-e2e_e2e_surrealdb_data`, and
  **dedicated ports**: frontend `AZOA_E2E_FRONTEND_PORT` (3200), API `AZOA_E2E_API_PORT` (5200).
  Nothing here reads `AZOA_FRONTEND_HOST_PORT`, which root `AGENTS.md` uses (3100) to let the dev
  stack coexist with another frontend. The dev stack and its `surrealdb_data` are never touched,
  so no `dev-up -Reset` is involved; the API entrypoint applies the schema to the E2E volume.
- `down` removes volumes only with `--wipe` or `CI=true`, and refuses unless the compose file
  declares the `azoa-e2e` project.
- **New secrets always mean a fresh volume.** The operator credential is seeded once per volume
  (`CredentialRevision` 1), so whenever `prepare`/`up` writes `.generated/e2e.env` (first run, the
  file was lost, or `--rotate`) the script prints that it is removing the `azoa-e2e` stack and
  volumes, then does so before anything starts. A kept volume never meets a new password.
- `prepare` (also run by `up`; needs a container engine) writes `.generated/` (gitignored): a
  throwaway CA, a server certificate whose SAN covers `host.docker.internal`,
  `host.containers.internal`, `localhost`, `127.0.0.1` (needs `openssl`; Git for Windows' copy is
  found automatically), `trust/` holding only the CA (as `ca.pem` and its OpenSSL
  `<subject-hash>.0`), `e2e.env` with random vendor and operator credentials, and
  `egress-probe/` (see §hermetic).
- Values resolve the same way on both sides: `process.env` first, then `.generated/e2e.env`
  (`scripts/e2e-stack.mjs`, `support/environment.ts`).
- The frontend image is `stack/frontend.Dockerfile` (build context: repo root): `azoa-sdk` is a
  `file:../sdk/azoa-wallet` dependency and the repo `.dockerignore` excludes `dist/`, so the image
  builds the SDK before `npm ci` in the frontend, in the `/repo/{sdk,frontend}` layout that
  `next.config.js` expects. It copies explicit paths and never `frontend/e2e/`.
- On podman set `AZOA_E2E_VENDOR_HOST=host.containers.internal` (and `AZOA_E2E_COMPOSE="podman
  compose"` if docker is also installed) for both `up` and the test run. On Windows the host must
  accept inbound connections on 9443/9444 from the container VM; the probe's positive control
  fails with "Connection refused" until it does (seen locally with podman machine 2026-10-16).

## §hermetic — why no journey can reach a real vendor

`docker-compose.e2e.yml` points `Kyc__Identomat__*BaseUrl` and `Fiat__ApiBaseUrl` at
`https://${AZOA_E2E_VENDOR_HOST}:9443/9444`, injects the generated credentials (`:?` makes a bare
compose up fail loudly), turns webhook delivery on (round 4) against the in-project
`webhook-receiver` (§webhook-receiver), and sets **both** `SSL_CERT_FILE` and
`SSL_CERT_DIR` to the mounted trust directory. .NET on Linux reads trust roots from OpenSSL's
`SSL_CERT_FILE` and `SSL_CERT_DIR`, and only falls back to `/etc/ssl/certs` when `SSL_CERT_DIR` is
not set (`.omc/research/dotnet-linux-x509-2026-10-16.md`), so the per-run CA is the only root.

Global setup proves it with **.NET itself**: `support/egress-probe.ts` runs
`dotnet /e2e/probe/AzoaEgressProbe.dll` inside `azoa-e2e-api` (same process environment as the
API). The probe's `HttpClient` must reach the fake Identomat (positive control) and must fail
against `external-api.identomat.com`, `widget.identomat.com` (the host D5 hardcodes) and
`api.stripe.com`; any violation aborts the run. Source: `stack/egress-probe/` (BCL only). It is
kept as `EgressProbe.csx` because the repo-root `AZOA.WebAPI.csproj` compiles every `**/*.cs`; the
script copies it to `Program.cs` and publishes it inside `mcr.microsoft.com/dotnet/sdk:10.0`, and
compose mounts the output at `/e2e/probe`.

The runner awaits an asynchronous `execFile` subprocess for the probe, bounded
to 120 seconds and 1 MiB of output per stream. Runtime discovery uses the same
non-shell API with a five-second bound per candidate. The Node event loop must
remain free: this same process hosts the fake HTTPS vendors that the positive
control calls, so a synchronous subprocess wait prevents the vendor from
answering and creates a false timeout. Numeric probe failures retain the control
report; launch, signal, timeout and output-limit errors still abort setup and
close the vendors. The configured Node child-process types document the
asynchronous callback, `timeout`, `maxBuffer` and direct executable semantics.

Executed locally in `aspnet:10.0` containers (2026-10-16): with both variables the probe reported
"certificate not trusted" for both Identomat hosts; with only `SSL_CERT_FILE` (the first-pass
configuration) `widget.identomat.com` answered a trusted response, which is why both are set.

## §fake-vendors — `support/fake-vendors/`

Node HTTPS servers on `0.0.0.0` (the API container dials them through the host gateway), started
by `global-setup.ts` for the whole run and closed by its returned teardown. They mirror the
backend fakes in `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/`:

- Identomat follows the documented **standard** configuration by default: `/begin` returns
  `{ id, url: "?session_token=<id>" }`, so the session id is the token. `AZOA_E2E_IDENTOMAT_MODE=
  additional-information` adds the child `additionalSessionId`; which token the widget result
  takes for that pair is undocumented, so no journey depends on it.
- Stripe honours `Idempotency-Key` like Stripe (parameter mismatch → 400 `idempotency_error`).

- Identomat also answers the documented widget `POST /external-api/delete/` (`true`, or
  `{ argumentError: "session-not-found" }`); a deleted session stops answering `/external-api/result/`.

Each exposes a `/__e2e/*` control plane that only the test runner calls (`support/vendors.ts`,
TLS verification skipped for that client alone).

## §webhook-receiver — where e2e webhook deliveries land

Delivery workers run in e2e (`Webhooks__Enabled=true`). The SSRF guard refuses private, loopback and
gateway addresses, so a host-run fake is unreachable for deliveries. Instead the `webhook-receiver`
compose service (`support/webhook-receiver/receiver.mjs`, no dependencies) sits at the static
public-range address `198.51.99.10` on the project-internal `e2e_webhooks` network
(`198.51.99.0/24`), and the API pins `webhook-receiver.e2e.azoa.test` to it through `extra_hosts`.
It serves TLS with the per-run vendor certificate, whose SAN list includes that name
(`scripts/e2e-stack.mjs`; an older `.generated/` needs `up --rotate` once), records each POST
(path, `x-azoa-*` headers, body) and answers 200. `FakeVendors.webhookDeliveries()` reads them
through the loopback control plane (`AZOA_E2E_WEBHOOK_RECEIVER_PORT`, default 9445). A journey can
therefore assert a real signed delivery instead of a dead-lettered event.

## §hosted-pages — interception, then the vendor's side

Identomat workflows do not exist yet (owner-authorized mocking), so the hosted pages are served
inside the browser by `context.route`: `FakeVendors.interceptIdentomatWidget` and
`interceptStripeCheckout`. The person presses "Complete verification" / "Pay" on that page; the
route handler then plays the vendor: set the verdict (or mark the session paid) through the
control plane and deliver the callback or the **signed** Stripe event
(`AzoaApi.deliverStripeEvent`, HMAC with the run's `AZOA_E2E_STRIPE_WEBHOOK_SECRET`). Nothing is
mocked inside the browser except the vendor's own page.

## §journeys

| Journey | Path through the product |
|---|---|
| `signup-and-identity-verification` | Register (UI) → operator issues tenant key → `/kyc` shows Identomat → member enrolled → hosted KYC (intercepted) → callback → member gate `Approved` |
| `tenant-business-verification` | Sign in (UI) → `/business-verification` → start → hosted KYB in a popup → callback → refresh → "Business verified" |
| `stripe-on-ramp` | Same tenant member provisioned/enrolled → tenant KYC/AML approved → member claims account → owner signs in and grants fresh consent → tenant starts checkout → hosted Stripe page → signed `checkout.session.completed` → intent `complete`, settlement `payment_received`/`funds_applied` |
| `webhook-registration` | Sign in (UI) → `/settings` → register `webhook-receiver.e2e.azoa.test` (pinned via compose `extra_hosts`, no DNS) → secret shown once → reload shows version 1 and no secret |

There is no first-party KYC screen and no "Add funds" screen, so those steps go through the
tenant API exactly as an integration would; the browser covers every screen that exists.

`completeBusinessVerification` reuses the hosted KYB journey before either
custodial-member enrollment flow. The tenant owner completes the real UI and
authoritative fake-vendor callback; a provider assignment alone grants no KYB
approval. A later member KYC interception deliberately registers the latest
handler for the same hosted-widget route. The webhook secret assertion selects
the exact textbox role/name: a broad label also matches the rotation checkbox.

Stripe provisions and enrolls the same tenant member, asserting the canonical
IDs match, then completes hosted KYC under that tenant's policy. The tenant status
must be `Approved`, which also requires clear AML lineage. Node-direct personal
KYC has different tenant provenance and cannot cover a tenant-key checkout.
After approval, the real claim-invite and anonymous password-claim routes let
that member take ownership. The fixture uses the server-returned synthetic email
for normal UI login; no delegated credential stands in for owner authority.
Claim revokes prior consent but preserves KYC/AML evidence, so the owner then
grants fresh `transfer:sign` through Settings with a bounded expiry. Provisioned
participation alone never includes this value scope. Checkout, intent reads and
signed settlement retain the original tenant flow and financial assertions.

### Current settlement limit — candidate 67628a9c

The latest complete collection passed four journeys and failed Stripe settlement.
The hermetic fake-vendor positive control and all three real-vendor negative
controls passed. Stripe now clears tenant KYC/AML, real account claim, UI login,
fresh owner consent, tenant checkout and the hosted payment. Its signed webhook
returns 500 because no verified platform unit exists on Devnet; the unchanged
200/202 delivery assertion correctly fails. Settlement and complete-intent
success are not established.

The existing simulated provider lacks both durable fungible creation and
recipient preparation. Those gaps and the checked settlement gates already
exist in upstream/main; no prior passing Stripe run is
established. A genesis call or a fabricated active platform-token row cannot
supply the required verified issuance lineage. Further real setup also requires
an operator-declared on-ramp budget, exact tenant price and both current KYC
contexts selected by the settlement/allocation path. The separate open
[hermetic settlement follow-up](../../conductor/tracks/node-admin-rbac-cli_20261018/hermetic-stripe-settlement-followup.md)
requires design review before capability or harness implementation. Preserve the
positive payment assertions and report this known red independently of passing
scoped node-admin checks.

## §writing-a-journey (round 4: NFT, DEX, Hold)

1. `import { expect, signIn, test } from '../support/fixtures'`; actors come from `api`
   (`registerPerson`, `makeTenant`, `assignIdentomat`, `enrolMember`, ...). Add routes to
   `support/api.ts` only if the API exposes them.
2. A third party reached through `IHttpClientFactory` is a new fake server under
   `support/fake-vendors/` with a `/__e2e/*` control plane, a port, a config override in
   `docker-compose.e2e.yml`, and a SAN already covered by the generated certificate. Chain RPC is
   not routable yet (the providers build their own `HttpClient`; see
   `tests/AZOA.WebAPI.IntegrationTests/Http/AGENTS.md` §vendor-edge): chain journeys run on the
   API's simulated chain mode until that product item lands. Never stub a provider inside the API.
3. Hosted or wallet pages are intercepted with `context.route`; the vendor's side runs in the
   route handler.
4. Assert what the person sees, then the API state they would see on reload.

## §ci

`.github/workflows/ci.yml` job `e2e`: installs frontend deps on the host (Playwright and the fake
vendors only; neither imports the SDK), caches `~/.cache/ms-playwright` by the Playwright version,
`e2e-stack.mjs up` (builds the egress probe in an SDK container; the frontend image builds the
SDK itself), `npm run test:e2e`, on failure
`e2e-stack.mjs logs` plus an upload of `e2e/.output/` (traces, screenshots, HTML report), and
always `e2e-stack.mjs down` (`CI=true` drops the project's volumes).
