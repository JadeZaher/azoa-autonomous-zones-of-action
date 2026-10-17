---
type: lane-checkpoint
lane: V
status: source-complete
owner: lane-v
---

# Round 3 — Lane V (verification harness) checkpoint

Owner decision 2026-10-16: four new test layers. Lane V built the harness and first flows;
round-4 lanes write their own slice/E2E specs on it. Ownership: `round3-claude-resume.md` §Lane V.

## Items closed

| # | Item | Evidence |
|---|---|---|
| V1 | Backend HTTP flow harness | `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/` (factory, `VendorEdge`, Identomat/Stripe fakes, `ServerLog`, fixture contract). First version compiled and exercised in a `bb512cf` worktree (removed). |
| V2 | Backend flows | `Http/*FlowTests.cs`: KYB, custodial KYC, reconciliation, Stripe money path / replay / refusals, webhook registration and withdrawal, recovery authorization. First run in the sweep. |
| V3 | MSW slice harness + slices | `frontend/src/test/msw/`, `frontend/src/test/slices/` (KYB, tenant KYC policy, webhook registration). |
| V4 | Vitest browser mode | `slices-browser` project (Playwright provider, Chromium). |
| V5 | Playwright E2E | `frontend/e2e/`, `frontend/playwright.config.ts`, root `docker-compose.e2e.yml` (project `azoa-e2e`); four journeys. First run in the sweep. |
| V6 | CI | `.github/workflows/ci.yml` jobs `frontend-browser`, `e2e`. |
| V7 | Harness smoke | After review fixes: jsdom slices 8/8, Chromium slices 8/8; `npm test` 179 pass / 4 fail, all 4 pre-existing outside lane V (vocabulary gate on feature copy, operator actions retry). |

## Review fixes (independent review, REQUEST CHANGES, 2026-10-16)

| # | Finding | Fix |
|---|---|---|
| H1 | Identomat fakes encoded the child-token assumption | Fakes default to the documented standard configuration (`{ id, url: ?session_token=<id> }`; id is the token); `AdditionalInformation` / `additional-information` is an explicit, unrelied-on variant. Forged-key KYC test now discriminates (forged → pending, genuine at the same vendor state → approved). D4 downgraded. |
| H2 | E2E trust not hermetic | `SSL_CERT_DIR` and `SSL_CERT_FILE` both point at a mounted trust directory holding only the CA (`ca.pem` + `<hash>.0`); global setup runs `support/egress-probe.ts` (fake vendor must succeed, real Identomat/Stripe must fail, from inside the API container). OpenSSL semantics checked locally with `s_client`. |
| H3 | CI e2e frontend image could not resolve `azoa-sdk` | `frontend/e2e/stack/frontend.Dockerfile` builds the SDK in the image (`/repo/{sdk,frontend}` layout); `e2e/AGENTS.md` §ci corrected. |
| M4 | No money-path HTTP flow | `StripeVendor.Seed` and a `checkout.session.completed`/`async_payment_succeeded` flow asserting the re-fetch decides and the payer reads `complete`/`payment_received`. Will hit D2 in the sweep. |
| M5 | Fixture guard leaked | Contract compares value kinds, object-vs-null and array element shapes; `withdrawal` and `identomatChoice` now checked; `fixtures/index.ts` uses a typed `fixture<T>()` builder (missing fields or wrong kinds fail typecheck). |
| M6 | Local E2E could pollute/wipe dev | `docker-compose.e2e.yml` is a standalone project `azoa-e2e` (own SurrealDB, volume, container names; ports now 5200/3200, see closing pass); volume removal only with `--wipe` or `CI=true`, refused unless the file declares that project. |
| M7 | Edge coverage | `ConfigureHttpClientDefaults` routes every factory client through the edge (SurrealDB passthrough only), plus explicit re-pointing of clients with their own primary handlers. Chain providers' own `new HttpClient()` recorded below. |
| L8 | Low items | Stripe fake: idempotency mismatch → 400 `idempotency_error` (both fakes); webhook journey uses a compose-pinned host, not real DNS; `process.env` wins over `e2e.env` on both sides; slice call-count asserts replaced by UI/state outcomes; recovery "allowed" excludes 404. |

## Closing pass (re-review cycle 1 APPROVED; four LOWs)

| # | Finding | Fix |
|---|---|---|
| C1 | Lost `e2e.env` + kept volume → stale operator password | `e2e-stack.mjs` wipes the `azoa-e2e` stack and volumes whenever `ensureSecrets` writes new secrets, and says so (`e2e-stack.mjs:211-217`). |
| C2 | Probe tested curl, not .NET | `stack/egress-probe/` (BCL-only `HttpClient` probe, `.csx` so `AZOA.WebAPI.csproj` never compiles it) built in an SDK container and run by global setup inside `azoa-e2e-api` (`support/egress-probe.ts`); must-fail list includes `widget.identomat.com`. Executed in `aspnet:10.0`: both Identomat hosts "certificate not trusted" with both variables; with only `SSL_CERT_FILE`, `widget.identomat.com` was trusted. Local positive control hit podman-on-Windows "Connection refused" to the host (documented). |
| C3 | Port collision with the dev 3100 recommendation | E2E uses dedicated `AZOA_E2E_FRONTEND_PORT` 3200 / `AZOA_E2E_API_PORT` 5200 and never reads `AZOA_FRONTEND_HOST_PORT` (compose, `playwright.config.ts`, `environment.ts`, `e2e/AGENTS.md`). |
| C4 | Arrays compared to the fixture's first element only | `FrontendFixtureContract.cs:40-50` checks every live element against the fixture and every fixture element against the live shape. |

Smoke after the closing pass: slices 8/8 jsdom, 8/8 Chromium.

## Product defects found (not fixed; feature source)

| # | Severity | Finding |
|---|---|---|
| D1 | **Compile blocker** | `Providers/Fiat/FiatRailProvider.cs:610` (and `Services/Fiat/OffRamp/PayoutAccountService.cs:236`) call `TryAttachProviderSessionAsync` passing `ct` where `creationToken` is now expected; neither claims/fences creation, so every attach would fail after a mechanical fix. |
| D2 | HIGH (executed) | `Services/Fiat/StripeWebhookInbox.cs:467-468` writes bare hex into `record<hosted_session>` / `record<user>` columns: every intent-correlated webhook answers 500. |
| D3 | HIGH (executed at `bb512cf`) | `Core/Surreal/SurrealRecordParam.cs` builds `SurrealRecordId("user", …)`, rejected by SurrealForge 1.2.0 as a reserved word; 30 call sites in 15 stores. `SurrealDb.Net.Models.RecordId.From` worked. |
| D4 | Downgraded | KYC result fetch sends the parent id as `session_token`: correct for Identomat's documented standard configuration. Confirm with the vendor for `additionalInformationRequest` configurations when workflows exist. |
| D5 | LOW (confirmed) | `Controllers/HostedSessionReconciliationController.cs:89` hardcodes `widget.identomat.com`; `:47` message mojibake "20â€“1000". |
| D6 | Plausible | Ethereum recovery reads answer 500 (factory throws) instead of their intended 409. |
| D7 | Plausible | Dev compose `Kyb__ReturnUrl` (http) makes KYB unconfigured; no first-party KYC route; no "Add funds" screen. |
| D8 | Plausible (static) | `FiatRailProvider.BeginCheckoutAsync` reuses `IdempotencyKey = azoa-fiat-checkout-{intent}` with a fresh `expires_at` on every retry after a lost create; Stripe answers `idempotency_error` for 24 h, so that intent can never open. |
| D9 | Product item | Chain providers construct `new HttpClient()` (`AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161`); they must take an injected handler/`IHttpClientFactory` before chain RPC can be faked or kept hermetic. |

## Round-4 carry-forward (why not round 3)

- Port the 18 `vi.mock('@/lib/azoa')` tests to slices (owned by N/B/C components;
  `frontend/src/test/AGENTS.md` §migration).
- NFT/WTT/Hold/DEX journeys and HTTP flows: need D9 first for RPC fakes; until then simulated
  chain mode (feature lanes).
- `funds_applied` leg of the on-ramp (needs a registered price schedule; lane B).

## Cross-lane edits

`frontend/package.json` scripts (`test` now selects `unit`+`slices`; adds `test:browser`,
`test:e2e`) and `msw.workerDirectory`; header comment of `ci.yml`.
