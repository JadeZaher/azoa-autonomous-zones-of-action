---
type: lane-checkpoint
round: 4
lane: qa-api
branch: r4/qa-api
base: feat/platform-stub-closeout@14bc9484
findings: [H5, H-A, H4, M2, M5, M3-api, L2, L6]
merged: pending
---

# Round 4 post-QA fix batch: r4/qa-api (backend half of live QA 2026-10-18)

Inputs: `qa-live-2026-10-18.md`, `round4-post-qa-deferrals.md` §Recorded during QA. Backend only
(`*.cs`, `docs/`, test projects). No `Program.cs` or `appsettings*.json` hunk, so there is no `## Hunks` section.
The parallel lane r4/qa-web owns `frontend/**` and `sdk/**`; the wire changes it needs are under §Needs other lane.

## DONE

### H5 HIGH: `GET /api/user/{self}` returned the bcrypt `passwordHash`
- Cause: `UserController.Get` returns `AZOAResult<object>`, so System.Text.Json serialized the runtime
  `Models.User`; the `[JsonIgnore]` sat only on `IUser.PasswordHash`, which does not apply to the class.
- Fix: new allowlist DTO `Models/Responses/UserProfileResponse.cs` (no `PasswordHash`, no `AuthNotBefore`)
  for the self view and for `Register`, `Update`, `AssignDappRole`; `Models.User.PasswordHash` also gets
  `[JsonIgnore]` as a second line. Swept every user-returning endpoint: Tenant child views (`ChildUserResponse`),
  wallet/OAuth auth (token DTOs), search (`SearchHit`, username/title only) already project.
- New gate `Architecture/ResponseCredentialGateTests` walks every action's declared response type for
  credential-named properties. It found one more real leak, fixed here: `IActionGraphBundle.PrivateKeyHash`
  (`GET api/action-graph-bundle/{id}`, readable on public bundles; nothing in the app writes it) is now
  `[JsonIgnore]`. Wallet export (`PrivateKey`, `SeedPhrase`) is the one allowlisted reveal (RecentFirstPartyLogin owner).
- `Controllers/UserResponseCredentialTests` serializes the four user responses with the API's JSON options
  and asserts no hash / `passwordHash` / `authNotBefore` (covers the `object`-typed blind spot of the gate).

### H-A HIGH (owner decision 2026-10-18): account-creating sign-ins follow the registration policy
- `WalletAuthManager.VerifyAsync`: an unbound wallet creates an account only when
  `SelfRegistrationPolicy.IsAllowed`; a bound wallet always signs in. Refusal is
  `SELF_REGISTRATION_DISABLED`, mapped by `WalletAuthController.Verify` to 403, and only reachable after the
  signature proof verified (proof failures stay a generic 401).
- Same class closed for federated sign-in: `ExternalLoginService.CreateUserForIdentityAsync` asks the same
  switch; the callback redirects with `error=registration_closed`. An existing binding still signs in.
- `SelfRegistrationPolicy.DisabledCode` added; `POST /api/user/register` refusals carry it too.
- `docs/NODE-HOST.md` §3 (both registration rows), §8.6 (first tenant on a closed node), §8.10 (table of the
  three paths, wire codes, the QA-node `AllowPublicSelfServiceInProduction=true` exception, verify step).
- Tests: `Auth/AccountCreationPolicyTests` (real controller + manager / real service over in-memory tables):
  table of 4 node policies x {wallet, OAuth}, bound wallet on a closed node, bound OAuth identity on a closed
  node, bad signature on a closed node stays 401.

### H4 HIGH: wallet users could not save their profile
- Generated handles (`wallet-…`, `oauth-…`, `onboard-…`) contain hyphens; both validators allowed only
  `[a-zA-Z0-9_]`. New `Helpers/UsernameRules.cs` is the single alphabet (`[a-zA-Z0-9_-]`, 3-50) with one clear
  message for length and characters.
- Widening alone would let anyone pre-register a deterministic wallet/OAuth handle and break that person's
  first sign-in at the `user_username` UNIQUE index, so the generated prefixes are reserved for NEW names:
  registration (validator + `UserManager.RegisterAsync`), tenant provisioning (`TenantManager`) and a profile
  rename refuse them; keeping your own generated handle passes. No data migration: existing accounts work.
- Tests: `Managers/GeneratedUsernameProfileTests` (wallet user saves profile resending its handle, through the
  validator and `UserManager.UpdateAsync`; cannot adopt another reserved handle; registration name table).

### M2 MEDIUM: `/operator/chains` Ethereum recovery → 500
- Cause confirmed from shipped config: `appsettings.json` disables every Ethereum network, so
  `BlockchainProviderFactory.GetProvider` threw `InvalidOperationException("Network Devnet is disabled…")`; the
  controllers caught only `BlockchainProviderNotFoundException`.
- Fix: `Providers/Blockchain/Ethereum/EthereumRecoveryResolution.TryResolveEthereumRecovery` (non-throwing
  `TryGetRailProvider` probe) used by both recovery controllers; refusal is `409` `ETHEREUM_RECOVERY_UNAVAILABLE`.
- Tests: `Controllers/UnconfiguredRailRefusalTests` with the real factory over the shipped `appsettings.json`
  (the old code throws there).

### M5 MEDIUM: any signed-in user could list every user
- `GET api/user` is now `[Authorize(Policy = "Operator")]`. The route inventory covers mutating routes only, so
  there is no inventory row to change; `UserResponseCredentialTests.The_user_directory_is_operator_only` pins
  the policy and the integration test `UserControllerIntegrationTests` now asserts operator 200 / user 403.
  No frontend page calls `getAllUsers`.

### M3 (API part): top-up with Stripe unconfigured
- New `FiatConsumerErrors.PaymentsNotConfiguredCode = "PAYMENTS_NOT_CONFIGURED"` ("Adding funds is not configured
  on this node."). `FiatConsumerErrors.Unresolved(options)` picks it when payment keys are missing, else the
  transient `RAIL_UNAVAILABLE`. Used by `TenantPayoutBalanceController.TopUp`, `FiatCheckoutController` and
  `FiatRailProvider.BeginCheckoutAsync`. Both controllers map it to 503; the tenant controller also stopped
  mapping `RAIL_UNAVAILABLE`/`RAIL_BUSY` from the rail to 400. Server never created a record on this path; the
  "Resume" record is client-side (qa-web).

### LOWs
- L2: Identomat `UnavailableReason` is now end-user copy ("Identity verification is not available on this node
  yet.") with no config key names; the names still reach the operator via `MissingConfigurationKeys`. Two
  `IdentomatKycProviderServiceTests` that asserted the old leak were inverted.
- L6: `PUT api/user/{foreign id}` → 404 (same as DELETE), before the manager runs. The `GET 404 / DELETE 403` on
  actions is not an oracle: the `DappDevelop` role gate answers 403 before any lookup, whether or not the action
  exists. No change.
- Gate ratchet: `SurrealQlSourceRulesTests` failed on the base (`FiatReversalEventHandler.cs` bare-id residue
  was already fixed by fix-money2); removed its `KnownViolations` entry as the ratchet requires.

## Verification
- `dotnet build azoa.sln -c Debug --no-incremental`: 0 errors, 17 warnings (≤ 25).
- `dotnet test tests/AZOA.WebAPI.Tests` filtered to Architecture (all gates incl. RouteAuthorization,
  CrossTenant, ValueGateCoverage, TransactionScope, SurrealQl, the new credential gate) + every touched area
  (User*, WalletAuth*, ExternalLogin*/ExternalAuth*, Kyc*, TenantManager*, SearchManager*, Fiat*, Federated*,
  AdminBootstrap*, Validator*, Ethereum*, ActionGraphBundle*): 1608 passed, 0 failed.
- Integration tests compile; not run (rule). `UserControllerIntegrationTests.GetAll_*` updated for M5.

## Self-review (security checklist on changed routes)
- Wallet 403 refusal is post-proof only, states a node fact; no user-existence oracle introduced.
- OAuth and wallet gates sit at the account-creation seam (service/manager), not only the controller.
- Directory gate uses the existing `Operator` policy (JWT-only, rejects API keys and acting tenants).
- Reserved prefixes close the squatting a wider alphabet would have opened; the unchanged-handle exemption is
  per-user (compares with the stored username).
- Ethereum probe swallows only configuration-shaped exceptions (`InvalidOperationException`/`ArgumentException`
  inside `TryGetRailProvider`); the refusal is honest, no fake empty list.
- No raw SurrealQL, no new catch-alls, no new usings warnings; doc rationale lives in `Controllers/AGENTS.md`,
  `Helpers/AGENTS.md`, `tests/.../Architecture/AGENTS.md`.

## Needs other lane (r4/qa-web: `frontend/**`, `sdk/**`)
- `POST /api/user/auth/verify` may now return **403 `SELF_REGISTRATION_DISABLED`** for a new wallet on a closed
  node: show the node message, not "network request failed".
- OAuth callback can redirect with `error=registration_closed`: add copy to `ERROR_COPY` in
  `frontend/src/app/(auth)/callback/page.tsx:17`.
- `PAYMENTS_NOT_CONFIGURED` (503) on `POST /api/fiat/tenant-payout-balance/top-up` and fiat checkout: render the
  `/funds`-style "not configured" state and do not keep a resumable pending record.
- `409 ETHEREUM_RECOVERY_UNAVAILABLE` (body `{isError, message, code}`) on `GET /api/operator/ethereum/recovery/{network}`:
  show "Ethereum is not enabled on this node" in `frontend/src/components/operator/chain-recovery.tsx:114`.
- Username alphabet now allows hyphens; names starting `wallet-`/`oauth-`/`onboard-` are refused unless unchanged.
  Profile save errors arrive as ProblemDetails `errors.Username[]` (H4 UI half).
- `GET /api/user` (SDK `getAllUsers`) is operator-only now; `UserResponse` has no `passwordHash`/`authNotBefore`.

## Deferred
- `POST /api/search` with `EntityTypes=User` still returns every user's `Username`/`Title` to any signed-in
  caller (`Managers/SearchManager.cs:48-74`, documented there as the intentionally public type; 5 unit tests and
  the `/search` page's User filter depend on it). Wallet handles embed an address prefix, so this is the same
  enumeration class as M5. Owner decision: scope user hits to self/tenant/operator, or keep marketplace
  discoverability. Reversal cost: one predicate + test updates.
- M4 (empty-body 403 from role policies such as `DappDevelop`): a reason body needs a global
  `IAuthorizationMiddlewareResultHandler`; left for a dedicated change. Frontend can gate authoring on role.
- L3 (`/api/kyc/status` 404 before start, 403 for non-tenant KYC pages, node-transparency checkpoint 503): changing
  statuses needs a coordinated SDK/frontend change; not done here.
- C1, H1-H3, M1, L1, L4, L5, L7, L8, L10 are frontend/SDK or owner items (qa-web / owner). L9 is a Railway
  variable cleanup for the owner.
