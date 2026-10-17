# Controllers — directory notes

## Precision-safe bridge amounts

Bridge request and response amounts are decimal strings on the JSON boundary.
Requests are accepted only when the exact string is in `1..ulong.MaxValue`; the
domain and provider layers retain `ulong`, while response serialization writes
the same value as a string so browser and SDK consumers cannot lose precision.

## Tenant-driven profile KYC

`GET /api/kyc/status/summary` reads the authenticated user from the credential
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
`Operator` policy only (therefore JWT-only), loads an existing target user,
and ignores arbitrary scope input by having none: it always stamps exactly
`tenant:provision,wallet:manage,kyc:read,kyc:submit`. It returns the raw key once
under `Cache-Control: no-store`; no value-signing or operator scope can enter.

Delegated child JWTs are default-deny: only endpoints carrying
`[DelegatedChildCredential]` accept them, behind a node switch, with live consent
(see `Services/Consent/AGENTS.md` §Delegated child credentials). Consent, user ownership mutations, API-key management, and wallet
ownership mutations require a first-party login JWT. Service-to-service wallet
setup uses the tenant custodial-account routes instead.

Raw platform-wallet recovery is a separate recent-login action. It rejects API
keys and tenant provenance, is financially rate-limited, emits audit events that
contain identifiers but never signing material, and returns `no-store` headers.

## §per-endpoint-signing-scope

Money-moving REST endpoints carry a per-endpoint, server-side signing-scope check
so a low-privilege scoped API key (e.g. `dapp:develop`, meant only for
action/holon authoring) cannot move the owner's assets. The tenant-driven consent
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
`/swap/quote`) carry no signing-scope check, but the holon/action-graph-bundle/nft read endpoints
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
mutually-distrusting authenticated users share one namespace and isolation is
APP-LAYER only. Holon / ActionGraphBundle / NFT reads were previously unscoped — any tenant
could enumerate or fetch-by-id ANY other tenant's rows. They are now **owner-or-
public** scoped.

### The policy

A row is readable iff `row.UserId == callerUserId || row.IsPublic`
(`IsPublic` defaults to false — a domain visibility opt-in on `IHolon`/`IActionGraphBundle`;
an NFT is a Holon with `AssetType=="NFT"`, so it uses the underlying holon's
`IsPublic`). A **null** `callerUserId` fails closed (public-only) — mirrors
`SearchManager`.

- **List/query** paths filter in-memory over the store result:
  `.Where(h => callerUserId.HasValue && h.UserId == callerUserId.Value || h.IsPublic)`.
- **Single-get** of a non-owned, non-public row returns a "not found"-style
  `AZOAResult` (`IsError=true`, "… not found.") — existence is NOT confirmed, so a
  private row cannot be probed by id.

### Where it lives

The scope predicate lives in the MANAGER (`HolonManager.CanRead`,
`ActionGraphBundleManager.CanRead`, `NftManager.CanRead`); controllers thread the authenticated
`GetUserIdFromClaims()` into the read methods as `Guid? callerUserId`
(added as a new parameter mirroring `SearchManager.SearchAsync(callerUserId)`).
Action node handlers pass `context.ActingUserId` (the RUNNER — consistent with the
ActingUserId invariant); the GateCheck / `$from` binding resolver paths pass the
acting user AND keep their existing owner-STRICT post-check (a predicate/binding
may name only the runner's OWN holons, never a public one owned by someone else).

### What is NOT touched

- Write/mutation paths — already IDOR-guarded (`IsOwnedBy` / `ActionGraphBundleAuthorizationError`
  prefixes / per-endpoint `HasSigningScope`). Only reads gained the scope.
- Internal cycle-detection (`HolonManager.EnsureNotDescendantAsync`) uses an
  UNFILTERED subtree walk (`CollectDescendantsAsync`) — a cycle can run through a
  private cross-tenant descendant, so the guard must see the full tree.
- Holon `Clone` cross-user template mechanic — own-or-public gated on the root, and the subtree copy
  skips descendants the caller cannot read (round 4; see `Managers/AGENTS.md` §holon-owned-parent).

## §dapp-composition-rbac

`DappSeriesController` and `DappCompositionController` intentionally split the
surface into two buckets:

- **Read paths** (`GET /api/dapp-series`, `GET /api/dapp-series/{id}`,
  `GET /api/dapp-series/{id}/actions`, `GET /api/dapp-series/{id}/validate`,
  `GET /api/dapp-series/{id}/manifest`, `GET /api/dapp-series/{id}/status`) are
  ordinary authenticated-user reads. They rely on manager ownership checks and
  must remain reachable without the `DappDevelop` policy.
- **Write paths** (`POST/PUT/DELETE` series + action mutations, plus
  `/compose`, `/generate`, `/deploy`) are DApp-authoring actions. Series/action
  mutations keep `Authorize(Policy = "DappDevelop")`; lifecycle actions
  (`/compose`, `/generate`, `/deploy`) use `Authorize(Policy = "DappManage")`.
  API-key policies require both the key scope and the owning user's current
  DApp role, so a stale key cannot keep DApp authority after a role downgrade.
- The same `DappDevelop` gate covers the action authoring routes (publish/unpublish,
  node/edge/dependency edits, instantiate) and ActionGraphBundle create/update/delete (round 4).
- `/deploy` (series and bundle) captures the caller's signing credential and passes it down to
  `IDeploymentService.StartWithCredentialAsync`; without one, native signing fails closed.

## user-dapp-rbac — role-assignment endpoint (`UserController`)

- `PUT api/user/{id}/dapp-role` sets a user's `DappRole`. Target id is the
  ROUTE id, never the body (IDOR rule). Body is `UserRoleAssignmentModel { Role }`.
- Policy `DappRoleAssignment`: a node-operator session with a recent `auth_time`, or
  `User.CanAssignDappRoles()` (`Helpers/ClaimsPrincipalExtensions.cs`): the CURRENT
  `dapp_role` is manager AND the caller is either a first-party login JWT or an API key
  that explicitly carries `dapp:manage` (round 4, audit-2 H2: a legacy empty-CSV key or a
  key scoped to anything else is refused). The manager receives `ActingIsOperator()` and
  the same `CanAssignDappRoles()` flag.
  - Operator may assign ANY role incl. manager (operator bootstrap of the first manager).
  - A manager may grant only developer/user, and may not change the role of a user who is
    CURRENTLY a manager (no manager-demotes-manager); that needs the operator.
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

## Allocation receipts

`GET /api/allocation/receipt` and `POST /api/allocation/receipt/reconcile`
require an API-key principal carrying `nft:mint`, a nonblank
`Idempotency-Key`, and the `financial` rate limit. The controller derives API
key and user context only from claims; it never accepts an actor, API-key id,
or operation id in a body/query value. Missing ledger bindings plus foreign or
mismatched operations map to the same 404 response; a caller-owned failed or
in-progress ledger without an operation remains readable. Dependency failures
are generic 503 responses. Reconcile is an observation request, never a replay
or value-operation retry.

## Hosted-session reconciliation

The hosted-session recovery queue and mutation routes require the `RecentNodeOperatorSession` policy.
Only aged unattached admissions are eligible. Recovery accepts an evidence reference
and explicit operator attestation, re-fetches the real vendor outcome before attach,
and persists actor, timestamp and decision evidence on the original admission.
Confirmed absence preserves that audit while releasing the unique admission key.
The queue is an allowlist projection; provider session tokens and redirect URLs
are omitted. See `Services/HostedSessions/AGENTS.md` for vendor ambiguity limits.

## Business verification and shared Identomat callbacks

`TenantBusinessVerificationController` permits first-party tenant owners and provisioning API
keys, rejects delegated-child provenance, and sources tenant identity only from subject claims.
Services recheck the active owner and a live provisioning key. API-key reads/refresh require
kyc:read; starts require kyc:submit. Redirect responses are no-store. Identomat's company-wide
callback at `/api/kyc/callback/identomat` routes across KYC and KYB using each flow's server-only
session correlation and one company callback secret. Unknown sessions and rejected signatures
receive the same acknowledgement. See `Services/Kyb/AGENTS.md` for setup and trust boundaries.

## Durable NFT API

Both NFT route families now delegate to the same lifecycle. UserNFT mint/transfer/refund use body feeAuthorization; burn uses quote/revision/maximum query fields. The legacy Nft mint/transfer/burn bodies also carry feeAuthorization. Nonzero quotes must be read and explicitly authorized before admission. Credential issue still requires manager role and mint scope; pending issuance/revocation returns HTTP 202 with its lifecycle result, and only confirmed completion is settled.

Authenticated participant lifecycle GET/list/refresh/accept/cancel routes expose recovery. Fee quote accepts nftId or sourceOperationId to resolve historical network identity. Request-key lookup cannot prove absence of an in-flight request; cancel-by-key writes an atomic permanent tombstone or applies existing lifecycle cancellation rules. Refund creation requires the original sender's completed transfer, while the current holder authorizes the reverse movement. No controller accepts a body-supplied logical issuer.

### Consent command receipts

User consent creation requires a caller-persisted requestId and actual first-party owner credential. The response is the original immutable ConsentLifecycleReceipt; it is not a current live-grant status. Replaying a successful command after revocation does not recreate consent. GET remains the current scoped grant view. DELETE returns the immutable ConsentRevocationReceipt. Participation requires prior tenant-approved enrollment. Tenant offboard endpoints return the original ConsentOffboardReceipt and close membership before any later admission can succeed. Actual principal capture and transactional authority fences apply to mutations; request body IDs cannot supply the actor.

NFT mint-cost-quote endpoints require the same authenticated NftMint scope and request key as mint. Credential issue cost-quote additionally requires the manager role and distinct recipient, and normalizes soulbound/nontransferable flags identically to issuance. Responses include the exact receipt hash and category limits as an acceptance payload; returning that payload does not itself authorize execution.

NFT workflow cost endpoints bind authenticated actor/tenant to the exact persisted execution. Quote and accept capture actual credentials; acceptance checks original workflow and current participant authority transactionally. Reconcile is an authenticated receipt read and does not demand new signing consent. Accept returns 202 because admission alone is not mint completion. See `Services/Actions/AGENTS.md` for saved-request and recovery semantics.

## Wallet ownership proof (round 4, audit-1 M2)

`POST /api/wallet/{id}/ownership-challenge` issues an ordinary wallet-auth challenge for
the caller's own External wallet; `POST /api/wallet/{id}/ownership-proof` consumes it,
verifies the signature (`WalletAuthManager.VerifyAddressControlAsync`) and records a
`wallet_ownership_proof` row (record id = sha256 of the canonical chain:address, so one
current prover per address; a newer verified proof supersedes). `connect` with
`SignedMessage` does the same inline. Unproven External rows grant nothing: create/connect
no longer refuse an address another user merely *registered* (that was the squat), only one
another user has *proven*. Consumers that trust an External address (WTT recipient, bridge
reversal, NFT recipient resolution) must require `IWalletOwnershipProofStore.GetAsync(...)
.UserId == actor` or a Platform wallet.

## Legacy bridge value routes (round 4, audit-2 M8)

`initiate`, `redeem` and `reverse` refuse tenant-delegated principals and require the
`transfer:sign` signing scope (`HasSigningScope`). Reversal releases only to one of the
bridge owner's wallets on the source chain: a Platform wallet or an External wallet with a
current ownership proof (`CrossChainBridgeService.OwnsReversalRecipientAsync`).

## Smaller round-4 hardening

- `HasSigningScope` treats a delegated child token (or any `act_as_tenant` principal) like a
  scoped API key: it must carry the scope.
- API-key rotate only rotates an active, unexpired, explicitly-scoped key whose scopes the
  caller may still self-issue; the old key is revoked first by an active-key CAS, so a
  concurrent second rotate loses.
- `GET /api/network` (anonymous) returns node/indexer URLs without credentials or query
  strings and withholds any URL with a key-shaped path segment.
- `POST /api/admin/key-rotation/rotate` additionally requires `RecentNodeOperatorSession`.
- Tenant child projections expose only correlation ids once the user has claimed the
  account (audit-1 M1); tenant provisioning never writes a tenant-supplied email into the
  unique email column (a tenant cannot prove a mailbox).
