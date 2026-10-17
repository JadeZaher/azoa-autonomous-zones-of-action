# src/test — frontend test harnesses

Three Vitest projects (`vitest.config.ts`) and one Playwright suite (`e2e/AGENTS.md`):

| Project | Command | What it proves |
|---|---|---|
| `unit` | `npm test` (with `slices`) | Component logic in jsdom; the original action-builder harness (`src/components/action-builder/AGENTS.md` §testing). |
| `slices` | `npm test` | A feature surface mounted alone with the **real SDK client**, network faked at HTTP level by MSW (Node). |
| `slices-browser` | `npm run test:browser` | The same slice files in real Chromium (Playwright provider) with a real Service Worker: real layout, real `fetch`, real `AbortSignal`. |

## §msw — `msw/`

- `api.ts` — `SLICE_API_ORIGIN` (`http://api.slice.test`, never resolvable, so an unintercepted
  request fails instead of reaching a host) and the `AZOAResult` envelope builders.
- `handlers/` — **stateful fake APIs**, one per controller, built from factories
  (`businessVerificationApi()`, `webhookRegistrationApi()`, `tenantKycApi()`). Each returns its
  MSW handlers plus the state a test drives (`decide('APPROVED')`, `loseNextResponse()`,
  `requireRecentLogin()`) or inspects (`beginKeys`, `received`). Status codes and bodies follow
  the named C# controller/store; `requireBearer` models `[Authorize]`.
- `fixtures/*.json` + `fixtures/index.ts` — see §fixtures.
- `network.ts` — the `SliceNetwork` both runtimes implement; records every API call (method,
  path, `Authorization`) and every call no handler answered.
- `node-network.ts` / `browser-network.ts` — `setupServer` / `setupWorker`. The worker script is
  generated into `msw/public/` (`npx msw init src/test/msw/public --save`; `package.json`
  `msw.workerDirectory` keeps it in step with the installed MSW) and served as Vite `publicDir`
  for the browser project only, so it never ships in `public/`.

MSW is pinned to **v2** (`^2.15.0`): v3 requires Node ≥ 22.12 and CI runs Node 20.

**jsdom quirk.** jsdom replaces `AbortController`, and Node's `fetch` (undici, inside MSW's
interceptor) rejects the foreign signal with *"Expected signal to be an instance of
AbortSignal"*. Even `new Request().signal` is jsdom-built there, so `node-network.ts` wraps the
intercepted `fetch` and drops `signal` for every call. SDK request timeouts are therefore only
exercised by `slices-browser`.

## §fixtures — one source of truth across the stack

The JSON files are shaped from the C# response models (each handler names its source) and are
**checked against live API payloads** by the backend HTTP flow tests
(`tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/FrontendFixtureContract.cs`: property
names, JSON value kinds, nested objects, array element shapes). On the frontend side
`fixtures/index.ts` builds each export with `fixture<SdkType>(json)`, whose parameter type is the
JSON-widened shape of the SDK type: a fixture missing a field (optional ones included) or carrying
the wrong primitive kind fails `npm run typecheck`. Literal values (`status: "APPROVED"`) are the
one thing a JSON import cannot narrow; the backend contract pins those against live payloads.
Change a fixture only together with the API model it mirrors.

## §slices — `slices/`

- `render-slice.tsx` — `renderSlice(ui, { apis, session })`: registers the fake APIs, signs the
  real SDK in (`azoa_token`/`azoa_user_id` in `localStorage`, then `azoa.session.restore()`),
  renders with Testing Library and returns `user` (user-event), `network` and `session`.
- `lifecycle.ts` — one lifecycle for both projects. **An API call no handler models fails the
  test that made it** (`afterEach` asserts `network.unhandled` is empty), which is how SDK↔API
  route drift surfaces without a running backend.
- `runtime.ts` — sets `window.__RUNTIME_CONFIG__` before `@/lib/azoa` is imported (the SDK
  singleton reads its origin at import time). `browser-process-shim.ts` gives Chromium the
  `process.env` that Next would have inlined.
- The slice projects resolve `azoa-sdk` to **`../sdk/azoa-wallet/src`**, not the `file:`
  dependency's `dist/` (only as fresh as the last `tsup` build). The `unit` project keeps the
  packaged SDK exactly as before.

Writing a slice: name it `*.slice.test.tsx` under `slices/`, mount the page or panel, drive it
like a user, assert what the user sees and what the fake API ended up holding (its state, e.g.
`api.current`, `api.status`). No `vi.mock` of `@/lib/azoa`, no assertions on SDK method calls or
request counts. Fake APIs enforce the real API's rules (the webhook fake refuses a reused request
id with a different command, as the store's command hash does), so a UI outcome such as "The
original change was accepted" proves the client resent the identical command. Slices so far: business verification (KYB),
tenant KYC provider policy, webhook registration.

## §migration — from `vi.mock('@/lib/azoa')` to slices

These tests mock the SDK module, so a route or payload change in the SDK or API cannot fail
them: `analytics/__tests__/page`, `user-nfts/page`, `action-builder/nft-workflow-costs`,
`action-builder/run/__tests__/run-panel`, `consent/consent-grants`,
`deployments/deployment-console`, `deployments/deployment-recovery`,
`fungible/fungible-creation-panel`, `hold/hold-recovery-panel`, `shared/nft-cancel-request`,
`shared/nft-lifecycle-panel`, `shared/nft-move-actions`, `shared/nft-pending-operations`,
`shared/nft-request-recovery`, `webhooks/webhook-events`, `webhooks/webhook-registration`,
`lib/__tests__/oauth-buttons`, `lib/__tests__/oauth`.

Path per test, owned by the lane that owns the component (N: NFT, B: hold, C: DEX/deployments):

1. Add a `msw/handlers/<controller>.ts` factory mirroring the controller's routes and status
   codes, and `msw/fixtures/<area>.json` shaped from the C# response model.
2. Add the fixture to the backend contract check in that lane's HTTP flow test.
3. Port the flow to `slices/<area>.slice.test.tsx` with `renderSlice`; keep the mocked test only
   for branches that are pure UI logic, then delete what the slice now covers.
4. Run both `npm test` and `npm run test:browser`.
