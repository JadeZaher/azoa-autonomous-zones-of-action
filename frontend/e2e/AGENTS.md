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
- `AZOA_E2E_PROJECT` selects a separate bounded project such as
  `azoa-payment-e2e-c17ff8f2`. Defaults remain `azoa-e2e`. A custom project stores credentials
  and certificates under `.generated/<project>/`, and derives its own container, volume and
  network names. The stack script inspects existing resources before preparation, reuse or
  removal; its saved owner marker must match every existing resource label. Unknown resources
  are refused, including older unlabelled stacks. Never adopt another user's project.
- For a parallel local project, inspect ports and network subnets first, then select unused
  frontend/API/vendor/control ports and `AZOA_E2E_WEBHOOK_SUBNET` plus
  `AZOA_E2E_WEBHOOK_RECEIVER_IP`. The defaults retain the original CI addresses. This only
  changes the test-owned internal webhook network; the real-vendor egress proof remains mandatory.
- A documentation-only local network such as `203.0.113.0/24` is suitable for the handoff
  and non-webhook journeys. `WebhookSsrfGuard` intentionally refuses all TEST-NET blocks,
  so the positive public-webhook journey is inapplicable there. Select explicit non-webhook
  journey files locally; the full CI job still runs every journey on its unchanged default
  network. Do not bypass the guard or relabel a denied delivery as success.
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
| `stripe-on-ramp` | Platform unit seeded (§settlement) → tenant KYB (UI) → tenant price registered → same tenant member provisioned/enrolled → tenant KYC/AML approved → the member's implied grant read back (`units:issue`, no `transfer:sign`) → tenant starts checkout → hosted Stripe page → signed `checkout.session.completed` → intent `complete`, settlement `funds_applied` |
| `webhook-registration` | Sign in (UI) → `/settings` → register `webhook-receiver.e2e.azoa.test` (pinned via compose `extra_hosts`, no DNS) → secret shown once → reload shows version 1 and no secret |
| `member-handoff` | Operator-only protected delivery → real resend retires the old link → browser scrubs fragment before preview → password claim → exact saved consent → fixed consumer return; separate lost-reply recovery and claimed-renewal decline cases |

The older on-ramp/KYC journeys retain their tenant API setup. First-party money screens have
separate browser slices; their green result does not prove the hosted handoff journey.

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

The payer stays **tenant-custodial and unclaimed**, the shape the integration
contract documents (§12.3.2: "a tenant-custodial child user never clicks
'allow' inside AZOA") and the ArdaNova BFF drives. `POST api/tenant/users`
(`TenantManager.ProvisionChildAsync` → `WithImpliedGrantAsync`) writes the
implied `Participation` grant (`tenant-provisioned:v1:` ref), and
`ConsentLifecycleIdentity.Normalize` unions the participation credit floor
(`units:issue`) into it. Checkout probes exactly `units:issue`
(`Controllers/AGENTS.md` §fiat-checkout-consent), so that grant covers it. The
journey reads it back through `GET api/tenant/consent` before checkout, so a
regression fails on the grant, not as an opaque 403.

Claim revokes the implied grant. An explicit `transfer:sign` grant alone does not cover
`units:issue`; the new hosted handoff requests the exact accepted five-scope descriptor.
Checkout, intent reads and signed settlement keep the original tenant fixture and financial
assertions, while the new handoff cases cover the separate claimed-member relationship.

## Hosted handoff coverage

`member-handoff.journey.ts` runs the real Next routes, SDK and API. It never calls claim or
approval from a setup API helper. Tenant receipts must contain metadata only; private links
come exclusively from the recent operator's protected delivery endpoint. E2E configuration
opts this isolated Development host into the stub and sets `ReturnTokenToTenant=false`.
Production flags and guards are unchanged. Its DataProtection ring is generated in its own
empty, labelled E2E volume; no production key backup or package publication is needed.

The resend case waits for the actual one-minute cooldown through bounded API polling; no
clock or row is changed. Every preview/read/decision request is paused before transmission
to observe an already-empty browser fragment, then continued to the real API. A lost claim
reply is induced only after `route.fetch()` receives server success and a public status read
confirms claimed ownership; the reply is then aborted. Recovery must use the same chosen
password through real owner login, make only one claim request and approve one exact grant.

The renewal case uses a fresh browser context, proves no claim token or second ownership
claim, signs in as the owner and declines the new request while the earlier grant survives.
All cases check the fixed return path. A browser route serves only the receiving consumer
landing page at the separately allowlisted loopback origin; it does not simulate ArdaNova's
withdraw implementation, any consent endpoint, verification provider or payment outcome.

Traces, screenshots and video are disabled for this file because private invitation URLs
and chosen passwords must not enter artifacts. Navigation errors are sanitized; privacy
assertions compare booleans rather than printing raw URLs or tokens. Tokens must be absent
from network URLs/referrers, console output, local storage and session storage. The expected
first-party JWT is allowed in the normal SDK session store. Bank-provider onboarding is not
claimed by these cases; only its post-approval account-holder action is rendered.
Private navigation and password entry use generic evaluate steps (native navigation/input
events), avoiding report step titles that embed `goto` URLs or `fill` values. Actual form
state, browser requests, SDK authentication and API effects remain real.

The sensitive file also sets `PLAYWRIGHT_NO_COPY_PROMPT=1`. The pinned Playwright worker's
`lib/index.js` checks this before taking the automatic ARIA error-context snapshot; ordinary
trace/screenshot/video options alone do not stop that snapshot from including password
textbox values. Safe error/source text can remain. An ignored intentionally failing canary
checks that neither the generated password nor a page snapshot enters the resulting files.
Keep this check when updating Playwright. Response observers retain rejection handling so
the original failure remains visible without a second unhandled promise after context close.
Alert assertions match the actual error text, excluding Next's empty route-announcer alert.

## Local build audit policy

The owner chose existing audit evidence. E2E frontend installs set `NPM_CONFIG_AUDIT=false`.
The stack script derives an ignored API Dockerfile from the actual production Dockerfile,
requires exactly one complete `RUN dotnet restore AZOA.WebAPI.csproj` anchor, and inserts
only ` -p:NuGetAudit=false`. Removing those inserted bytes must recover the original bytes
exactly; `api-Dockerfile-proof.json` records both SHA256 values and the inverse-byte result.
The production Dockerfile, publish command, tool-install command and runtime are unchanged.
The BCL egress-probe publish also passes the explicit property because publish can restore.

The released [.NET 10 tool downloader](https://github.com/dotnet/sdk/blob/v10.0.100/src/Cli/dotnet/ToolPackage/ToolPackageDownloader.cs)
installs its tool package through direct download/extraction, not an MSBuild PackageReference
restore/audit; installed `dotnet tool install --help` exposes no audit property switch.
Project-restore auditing and its opt-out are documented by [NuGet](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).
Do not blindly add unsupported MSBuild switches to tool installation. Dependency downloads
still occur as required for these approved local builds; no audit command is run.

## §settlement — the on-ramp rail on the E2E stack (P6-19)

The signed `checkout.session.completed` delivery is settled by `Services/Fiat/OnRamp` on the
Simulated chain, the same rail `tools/dev-node/dev-node-up.ps1` runs (its AGENTS.md §arda-genesis):

- **Config.** `docker-compose.e2e.yml` pins the Simulated economy explicitly (`Blockchain__Mode`,
  `AZOA__Simulated__PlatformSeed` = the committed public dev seed, `Reserve__Chains` with
  `Simulated`, `Reserve__ExpectedAddresses__Simulated` = the address that seed derives,
  `Exchange__Activation__Simulated__Devnet`, `Fiat__OnRamp__ReservePartition`). They equal
  `appsettings.Development.json`, which the container also loads; pinning them means a dev-config
  edit cannot silently take the rail away from this journey. The NFT zero-fee policy is not
  needed by the on-ramp and stays inherited.
- **Seed, through the public surface only.** `AzoaApi.ensurePlatformUnit()` mirrors
  `Ensure-ArdaGenesis` + `Ensure-ArdaOnRampPartition`: operator genesis (`Simulated`/`Devnet`,
  default definition: ARDA, 2 decimals), its own operation driven quote → accept-quote → continue →
  refresh to `Completed`, then the `on-ramp` partition at 1,000,000,000 base units. Idempotent on a
  kept volume (an existing unit short-circuits; re-declare SETS). `registerTenantPrice` then has the
  KYB-verified tenant register its own exact price (`"100"` base units per cent) for that asset;
  node-default fallback stays off. No row is written directly and no proof lineage is fabricated.
- **Assertion.** Delivery must be owned (200/202, unchanged), the intent `complete`, and the
  settlement state must reach `funds_applied`, which `FiatRailProvider` projects only from an inbox
  row the handler APPLIED (issuance `Transferred` with a tx hash, lot `Issued`). `payment_received`
  alone now means charged-and-uncredited and fails the journey.
- **Local compose evidence.** This journey passed in the isolated local compose project on
  2026-11-01, including genesis over HTTP, price, KYC-scoped allocation, Simulated transfer and
  applied inbox projection. Its signed incoming Stripe callback is separate from the public
  outbound webhook-delivery journey excluded on the local documentation-only subnet.

## Local hosted-flow validation — 2026-11-02

All three new hosted scenarios have passing real-browser evidence from the dedicated project.
The repaired three-case run passed resend/claim/approval/return and committed-lost-reply login
recovery; a final renewal-only run passed after the same-page consent login refresh fix. The
original signup/KYC, tenant KYB and Stripe on-ramp passes were retained without rerunning them.
The public webhook journey remains included in full CI and was excluded only from this local
203.0.113.0/24 run. The validator check passed 17 new matrix cases and three existing tests.
An intentionally failing privacy canary confirmed no password, ARIA snapshot or media artifact;
lint/type checks and the affected API/frontend image builds also passed. Images used the reviewed
uncommitted validator/component overlays on the base Git revision, not a pure revision checkout.

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
