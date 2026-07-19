# Controllers — directory notes

## Precision-safe bridge amounts

Bridge request and response amounts are decimal strings on the JSON boundary.
Requests are accepted only when the exact string is in `1..ulong.MaxValue`; the
domain and provider layers retain `ulong`, while response serialization writes
the same value as a string so browser and SDK consumers cannot lose precision.

## Tenant-driven profile KYC

`GET /api/kyc/status/summary` reads the authenticated avatar from the credential
subject. Every API key and every tenant-driven child credential must carry the
literal `kyc:read` scope; issuance of a child credential remains tenant- and
live-consent-scoped. The response is a minimal latest-submission projection.
Detailed status, submission, get-by-id, and document-reference routes require a
`FirstPartyLogin` JWT, even when an API key carries `kyc:read` or `kyc:submit`.
Service integrations use the purpose-built custodial-account routes below.

The service-to-service onboarding surface lives under
`/api/tenant/custodial-accounts/{externalSubject}`. `TenantScope` supplies the
tenant from the authenticated API-key subject; the route never accepts a tenant
id. Ensure additionally requires `wallet:manage` + `kyc:read`, status requires
`kyc:read`, and KYC begin/submit require `kyc:submit`. Tenant KYC responses are
purpose-built projections: document references, reviewer fields, provider
payloads, ciphertext, seeds, and private keys never cross this boundary.
Concurrent requests that observe an owned operation still in progress return
HTTP 409 with a retryable discriminator; they are not malformed 400 requests.

The first tenant key is issued through `POST /api/apikey/tenant`. The action is
`Operator` policy only (therefore JWT-only), loads an existing target avatar,
and ignores arbitrary scope input by having none: it always stamps exactly
`tenant:provision,wallet:manage,kyc:read,kyc:submit`. It returns the raw key once
under `Cache-Control: no-store`; no value-signing or operator scope can enter.

Delegated child JWTs are temporarily disabled at validation and issuance. They
remain default-deny until every future accepting endpoint has an explicit scope
policy. Consent, avatar ownership mutations, API-key management, and wallet
ownership mutations require a first-party login JWT. Service-to-service wallet
setup uses the tenant custodial-account routes instead.

Raw platform-wallet recovery is a separate recent-login action. It rejects API
keys and tenant provenance, is financially rate-limited, emits audit events that
contain identifiers but never signing material, and returns `no-store` headers.

## §per-endpoint-signing-scope

Money-moving REST endpoints carry a per-endpoint, server-side signing-scope check
so a low-privilege scoped API key (e.g. `dapp:develop`, meant only for
quest/holon authoring) cannot move the owner's assets. The tenant-driven consent
gate only fires on principals carrying `act_as_tenant`, which plain `X-Api-Key`
principals never have — hence this second, per-endpoint enforcement.

### Guarded endpoints and their required scope

| Endpoint | Action | Required scope |
| :--- | :--- | :--- |
| `POST /nft/mint` | `Mint` | `nft:mint` (`AzoaScopes.NftMint`) |
| `POST /nft/fungible-mint` | `FungibleMint` | `nft:mint` |
| `POST /nft/{id}/transfer` | `Transfer` | `transfer:sign` (`AzoaScopes.TransferSign`) |
| `POST /nft/{id}/burn` | `Burn` | `nft:mint` (Burn→nft:mint per `AzoaScopes.OperationScopeMap`) |
| `POST /wallet/{id}/topup` | `TopUp` | `wallet:manage` (`AzoaScopes.WalletManage`) |
| `POST /swap/execute` | `ExecuteSwap` | `swap:sign` (`AzoaScopes.SwapSign`) |

Read-only endpoints (`/nft` GET/query, `/wallet` get/list/query/portfolio,
`/swap/quote`) carry no signing-scope check, but the holon/star/nft read endpoints
ARE owner-or-public scoped — see §cross-tenant-read-scope. `POST /wallet/{id}/export`
is a recent first-party recovery action and never accepts an API key or tenant
principal.

### Lock-out-avoidance semantics (`HasSigningScope`)

The check must NOT block legitimate first-party callers. It is NOT the naive
`!User.HasScope(...)` used by `AllocationController` — Allocation is tenant/
API-key-ONLY, so it can hard-require a scope; these endpoints are reachable by
JWT owners and legacy full-access keys, which carry no `scope` claims.

`HasSigningScope` replicates the production-shipped `DappDevelop` authorization
policy (`Program.cs`) exactly, in the same order:

1. Not an API key (`AuthMethod != "ApiKey"`, i.e. a JWT owner) → **PASS** (unaffected).
2. `ScopesRestricted=true` marker (CSV was non-empty but every token was dropped as
   forbidden — hardening review M3) → **DENY** (must not fall into legacy full-access).
3. Empty scope set (genuinely-empty CSV = legacy full-access key —
   `ApiKeyAuthenticationHandler` emits zero `scope` claims) → **PASS**.
4. Scoped key → **PASS** iff it carries the required scope; else 403.

So only a key that carries SOME scopes but LACKS the required signing scope is
denied. JWT owners and empty-CSV legacy full-access keys are never blocked.

The helper is duplicated privately in each controller (identical body) rather than
added to the READ-ONLY `Helpers/ClaimsPrincipalExtensions.cs`. If it grows a
fourth consumer, promote it there. Each endpoint composes its own 403 with the
correct `AZOAResult<T>` envelope type for that action's return type.

## §cross-tenant-read-scope

In the multi-tenant "many dApp-devs on one SurrealDB namespace" deployment, two
mutually-distrusting authenticated avatars share one namespace and isolation is
APP-LAYER only. Holon / STARODK / NFT reads were previously unscoped — any tenant
could enumerate or fetch-by-id ANY other tenant's rows. They are now **owner-or-
public** scoped.

### The policy

A row is readable iff `row.AvatarId == callerAvatarId || row.IsPublic`
(`IsPublic` defaults to false — a domain visibility opt-in on `IHolon`/`ISTARODK`;
an NFT is a Holon with `AssetType=="NFT"`, so it uses the underlying holon's
`IsPublic`). A **null** `callerAvatarId` fails closed (public-only) — mirrors
`SearchManager`.

- **List/query** paths filter in-memory over the store result:
  `.Where(h => callerAvatarId.HasValue && h.AvatarId == callerAvatarId.Value || h.IsPublic)`.
- **Single-get** of a non-owned, non-public row returns a "not found"-style
  `AZOAResult` (`IsError=true`, "… not found.") — existence is NOT confirmed, so a
  private row cannot be probed by id.

### Where it lives

The scope predicate lives in the MANAGER (`HolonManager.CanRead`,
`STARManager.CanRead`, `NftManager.CanRead`); controllers thread the authenticated
`GetAvatarIdFromClaims()` into the read methods as `Guid? callerAvatarId`
(added as a new parameter mirroring `SearchManager.SearchAsync(callerAvatarId)`).
Quest node handlers pass `context.ActingAvatarId` (the RUNNER — consistent with the
ActingAvatarId invariant); the GateCheck / `$from` binding resolver paths pass the
acting avatar AND keep their existing owner-STRICT post-check (a predicate/binding
may name only the runner's OWN holons, never a public one owned by someone else).

### What is NOT touched

- Write/mutation paths — already IDOR-guarded (`IsOwnedBy` / `STARODKAuthorizationError`
  prefixes / per-endpoint `HasSigningScope`). Only reads gained the scope.
- Internal cycle-detection (`HolonManager.EnsureNotDescendantAsync`) uses an
  UNFILTERED subtree walk (`CollectDescendantsAsync`) — a cycle can run through a
  private cross-tenant descendant, so the guard must see the full tree.
- Holon `Clone` cross-avatar template mechanic — already own-or-public gated.

## §dapp-composition-rbac

`DappSeriesController` and `DappCompositionController` intentionally split the
surface into two buckets:

- **Read paths** (`GET /api/dapp-series`, `GET /api/dapp-series/{id}`,
  `GET /api/dapp-series/{id}/quests`, `GET /api/dapp-series/{id}/validate`,
  `GET /api/dapp-series/{id}/manifest`, `GET /api/dapp-series/{id}/status`) are
  ordinary authenticated-avatar reads. They rely on manager ownership checks and
  must remain reachable without the `DappDevelop` policy.
- **Write paths** (`POST/PUT/DELETE` series + quest mutations, plus
  `/compose`, `/generate`, `/deploy`) are DApp-authoring actions. Series/quest
  mutations keep `Authorize(Policy = "DappDevelop")`; lifecycle actions
  (`/compose`, `/generate`, `/deploy`) use `Authorize(Policy = "DappManage")`.
  API-key policies require both the key scope and the owning avatar's current
  DApp role, so a stale key cannot keep DApp authority after a role downgrade.

## avatar-dapp-rbac — role-assignment endpoint (`AvatarController`)

- `PUT api/avatar/{id}/dapp-role` sets an avatar's `DappRole`. Target id is the
  ROUTE id, never the body (IDOR rule). Body is `AvatarRoleAssignmentModel { Role }`.
- Method-level `[Authorize]` only (both JWT and API-key principals may authenticate);
  authority is decided in the manager from two controller-computed flags:
  - `ActingIsOperator()` mirrors the `Operator` policy EXACTLY (JWT-only floor +
    explicit operator:admin/Admin signal). Operator may assign ANY role incl.
    manager — this is the operator-bootstrap path that creates the first manager.
  - `User.HasDappManagerRole()` (the CURRENT `dapp_role` claim, which the real
    ApiKey handler re-reads from the owner's live store role) — a manager may grant
    only developer/user, never manager/operator. Using the role claim (not
    scope-or-role) keeps a stale-scope key fail-closed after a demotion.
- CRITICAL: operator:admin can NEVER be assigned. `AzoaDappRoles.IsAssignableRole`
  rejects anything outside dapp:user/developer/manager BEFORE authority is checked,
  so no request can set a role that yields operator:admin.
- Denials return 403 (Forbidden); an unknown target returns 404.

## node-operator-governance

`NodeGovernanceController` (`/api/node-governance`) is guarded by the dedicated
`NodeGovern` policy, not the broader `Operator` policy. The capability is
JWT-only and is stamped only by the sovereign operator bootstrap path; API keys
cannot mint or satisfy it. The controller never accepts an actor id in the body:
updates source the actor from claims and pass it to `NodeGovernanceManager`,
which writes the parameter row and audit row together.

Fee routes (`fee-schedule`, `fee-audit`) use the same policy and actor rules.
`NodeFeeScheduleUpdateRequest.ExpectedVersion` is the optimistic-concurrency
token. An identical retry is a no-op success; a stale version that requests
different values returns a conflict-shaped manager error and writes no audit.

Treasury routes (`treasury/{chain}/{network}`, `treasury`, `treasury-audit`)
also use `NodeGovern`. Treasury destinations are separate, versioned policy
records per chain/network rather than fee-schedule fields. PUT takes the actor
only from claims, maps stale differing `ExpectedVersion` writes to HTTP 409, and
keeps provider/address validation in `NodeTreasuryManager`. An identical address
retry is an idempotent success and does not append another audit row.

`NodeTransparencyController` is the deliberately separate anonymous read surface
at `/api/node-transparency`. It has no mutation actions and does not reuse the
operator DTOs. The global IP-partitioned rate limiter still applies (trusted
forwarder configuration remains a deployment gate), responses are short-cache
ETagged, and `SuppressDebugExceptionDetails` guarantees that an
unexpected failure stays generic even when a development node enables verbose
errors. Its named, credential-free CORS policy allows any browser origin and
short-circuits API-key lookup; arbitrary `X-Api-Key` values neither change the
anonymous IP partition nor trigger a pre-limit store read. The operator
controller remains `NodeGovern`-protected.
