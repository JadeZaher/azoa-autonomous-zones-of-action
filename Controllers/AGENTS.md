# Controllers — directory notes

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
is unchanged — it already Forbids tenant principals outright (raw-key export is a
user-only action).

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
