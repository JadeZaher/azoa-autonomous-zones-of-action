# Logout Flow + dApp-Developer API Keys — Design & Plan

**Date**: 2026-07-06
**Status**: Proposed — awaiting review before implementation
**Author**: design pass (auth audit + tenant-fit analysis)

This document covers two related asks:

1. A **logout flow** for the Azoa frontend (and avatars in general).
2. **API keys that let an avatar develop their own dApps** — a key grants
   edit rights to *that avatar's own* resources (holons, quests, STAR-ODK),
   never platform admin and never another avatar's resources.

It also folds in **4 access-control gaps** an authorization audit surfaced,
because two of them sit directly in the API-key blast radius.

---

## 1. Current state (verified, with file:line)

### Auth model
- **JWT is stateless**, 24h lifetime (`AvatarManager.cs:126`). No session table,
  no jti blocklist. Claims: `sub`=avatarId, `email`, `Name`=username, `jti`.
- The **only** server-side revocation lever is a per-avatar `AuthNotBefore`
  watermark (`Avatar.cs:38`), checked on every request in `Program.cs:157-202`
  (`OnTokenValidated`): a token issued before the watermark fails, fail-closed.
  Built for the sovereignty claim-flow; it is **avatar-wide** (kills all that
  avatar's tokens), not per-token.
- Frontend stores the raw JWT in `localStorage` under `azoa_token`
  (`frontend/src/lib/azoa.ts:44-84`).
- **Logout already exists client-side**: `azoa-context.tsx:165` →
  `SessionManager.logout()` (`sdk/azoa-wallet/src/client/session.ts:110`) clears
  `azoa_token`/`azoa_avatar_id` from storage + resets React state. There is **no**
  server logout endpoint.

### API keys
- `ApiKey` = `(AvatarId, Name, KeyHash, KeyPrefix, ExpiresAt?, Scopes CSV, ...)`
  (`Models/ApiKey.cs`). **Tied to an avatar only — no dApp/app linkage.**
- `ApiKeyAuthenticationHandler` emits `scope` claims from the CSV, strips
  `operator:admin` (`AzoaScopes.IsApiKeyIssuableScope`), and marks the principal
  `AuthMethod=ApiKey`.
- **Empty CSV = full access** for non-tenant legacy keys (`AzoaScopes.cs:10-13`).
- **Issuance is unvalidated**: `ApiKeyController.cs:53` stores whatever scopes the
  caller sends (except it can't *emit* `operator:admin`). Any avatar can mint
  itself a `tenant:provision` key today.
- Scope enforcement points today: `TenantScope` + `Operator` policies
  (`Program.cs:234-254`), one inline `HasScope` in `AllocationController.cs:55`,
  the signing seam in `KeyCustodyService`. **Holon / Quest / DappSeries writes are
  bare `[Authorize]` + manager-layer ownership — no scope gate.**

### dApp concept
- **No OAuth client / app-registration entity exists.** `DappSeries`
  (`Persistence/SurrealDb/Models/DappSeries.cs:50`) is the "STAR-ODK + quest series"
  aggregate, **avatar-owned**, and is the natural "dApp" boundary.
- **Tenant/ConsentGrant is a two-party delegation primitive** ("custodian acts on
  another user's resources with consent"); a tenant has **zero standing authority**
  of its own (`TenantManager.cs:16-30`).

---

## 2. Key design decisions

### D1 — Logout: ship "log out everywhere" (server), keep local logout
- Local logout (clear localStorage) already works — it's the "log me out of this
  tab" case. Keep it as the default button.
- Add **`POST /api/avatar/logout`** that bumps the caller's `AuthNotBefore` to
  `UtcNow`, invalidating **all** that avatar's live JWTs via the existing
  `OnTokenValidated` check. This is the "log out of all devices / panic button".
- **Not building** per-device jti revocation — that would reverse the deliberate
  stateless-JWT design to buy device-granular logout nobody asked for.

### D2 — dApp keys: avatar-scoped capability keys (NOT Tenant, NOT per-dApp yet)
Chosen scope (per product decision): **the simplest model that is still safe.**
- A "dApp developer" key is an ordinary API key owned by the developer's avatar,
  carrying **capability scopes** (e.g. `holon:edit`, `quest:edit`,
  `dappseries:edit`) that grant edit rights across **everything that avatar owns**.
- **No `DappSeriesId` fencing yet.** A `dapp:{seriesId}:*` resource-scope
  convention is a documented future step (§5), not in this cut.
- **Tenant/ConsentGrant is reserved** for the genuine multi-user-custodian case.
  A single owner acting on their own resources needs no consent ceremony.
- **`operator:admin` stays JWT-only and API-key-blocked** — node operators keep
  their own admin scope for key rotation etc. Unchanged.

### D3 — Make capability scopes real (enforce + validate at issuance)
Introducing capability scopes is only meaningful if they are **enforced** and
**can't be self-granted beyond what you own**:
- **Enforce**: add scope checks on the Holon/Quest/DappSeries **write** paths so
  a key that carries capability scopes is *restricted* to them. Preserve the
  "empty CSV = full access" legacy rule so existing keys and JWTs are unaffected —
  capability scopes only *narrow* a key when explicitly present.
- **Validate at issuance**: `ApiKeyController.Create` must reject scopes the caller
  isn't entitled to (mirroring how `tenant:provision` *should* be gated). Close the
  `ApiKeyController.cs:53` self-grant gap.

---

## 3. Product model: public dApp marketplace (reframes the audit)

The owner clarified the intended model, which **reframes two audit findings**:

> Everyone can **design and own** their own dApps. Others can **read** those
> quests/holons as **public references/templates**. **Starting** another avatar's
> quest gives you a **copy executing under YOUR avatar's context**, linked back to
> the origin template so you can claim rewards / hook into the blockchain ecosystem.

Consequences:
- **Cross-avatar READ of holons/quests is intentional** (the marketplace). Audit
  Finding 5 (unscoped holon reads) is **by design** — not a gap.
- **`POST /holon/{id}/clone` on a holon you don't own is the intended template
  mechanic**, not an IDOR bug — the clone is stamped to the caller and drops the
  on-chain `TokenId` (`HolonManager.cs:378,382`). What's **missing** is a
  **link-back/provenance** field (source holon + origin avatar) so rewards can hook
  in. So Finding 1 flips from "add an owner check" to "add provenance, keep it open."
- **The genuine leak is PII only**: email / real name must not be exposed via the
  public read surface.

### Gaps to fix first (revised for the marketplace model)

| # | Sev | Endpoint | Fix |
|---|-----|----------|-----|
| 1 | MED | `POST /holon/{id}/clone` orphan copy, no origin link (`HolonManager.cs:362`) | KEEP cross-avatar clone (it's the template mechanic); **add provenance** (`SourceHolonId` + origin avatar). Do NOT add an owner-only gate. |
| 2 | MED | `GET /avatar/{id}` + `GET /avatar` leak email/real-name PII (`AvatarManager.cs:66,71`) | Return a **public projection** (display/username only) for avatars other than self; full record only for self. Gate `GetAll` behind Operator or make it a public-projection list. |
| 3 | LOW | `POST /search` passes no avatar id (`SearchController.cs:21`) | Marketplace search over public objects is fine; ensure it returns the **public projection** (no PII) and doesn't leak private fields. |
| 4 | LOW | KYC admin uses inline `IsAdmin()` w/ no scheme floor (`KycController.cs:144`) | Route behind `[Authorize(Policy="Operator")]` (or a new `Admin` policy). |

### Quest-start-as-template flow (BUILD NOW — full mechanic)
**Traced state**: `Execute` is owner-only by construction — `LoadOwnedQuestAsync`
rejects non-owners (`QuestManager.cs:376`), and `QuestRun.AvatarId` is stamped from
the **quest owner**, not the runner (`:420`; durable path `:1617`). `QuestRun` has
fork lineage (`ParentRunId`) but **no cross-avatar origin fields**. So starting
someone else's quest is impossible today.

**Decision**: build the full mechanic with an **`IsPublic` flag** (quests default
private; creator opts in to publish).

Changes:
1. `Quest`: add `IsPublic` (default `false`) — domain + POCO + schema + goldens.
2. `QuestRun`: add `SourceQuestId` + `OriginAvatarId` — domain + POCO + schema +
   goldens + an index for reward lookups.
3. `ExecuteAsync` (`QuestManager.cs:374`): if caller owns the quest → today's path.
   Else require `quest.IsPublic && quest.Status==Active` (else 403/404). On the
   non-owner path stamp `run.AvatarId = avatarId` (runner), `run.SourceQuestId =
   quest.Id`, `run.OriginAvatarId = quest.AvatarId`.
4. `StartWorkflowRunAsync` (durable path, `:1617`): same re-stamp + provenance.
5. `GetAsync` (`:157`) is owner-only — add a public-read path so a non-owner can
   read a public quest to run it (mirror the already-public template reads).
6. Template instantiate: denormalize `OriginAvatarId` onto the instantiated `Quest`
   (today only reachable via `TemplateId`→`QuestTemplate.AuthorAvatarId` join).

---

## 4. Implementation plan

### Phase A — security fixes (independent, land first)
1. `HolonManager.CloneAsync`: reject when source `!IsOwnedBy(avatarId)`.
2. `AvatarController.Get`: return self freely; for other ids either 403 or return a
   minimal public projection (decide — see Open Questions). `GetAll`: gate behind
   `Operator` policy or delete if unused by the frontend.
3. `SearchController`: read `SearchManager` — add avatar scoping if cross-avatar.
4. `KycController`: replace inline `IsAdmin()` with `[Authorize(Policy="Operator")]`.

### Phase B — logout everywhere
5. `AvatarManager`: add `LogoutEverywhereAsync(avatarId)` that sets
   `AuthNotBefore = UtcNow` and persists.
6. `AvatarController`: `POST /api/avatar/logout` `[Authorize]`, subject from token.
7. SDK: `AzoaAuthProvider.logoutEverywhere()` → new endpoint, then local
   `SessionManager.logout()`. Rebuild SDK (tsup) so frontend resolves it.
8. Frontend: add "Log out of all devices" in the account menu next to the existing
   local "Log out". Wire to `logoutEverywhere()`.

### Phase C — dApp capability keys
9. `AzoaScopes`: add one coarse **`DappDevelop="dapp:develop"`** scope covering
   holon + quest + dappseries edit (owner's decision — one checkbox for devs). Add
   it to an `IssuableCapabilityScopes` set the issuance validator checks against.
10. Enforce: on Holon/Quest/DappSeries write actions, if the principal is an API
    key with a non-empty CSV, require the matching capability scope (empty CSV =
    full access preserved). Prefer a small policy/helper over scattered inline
    checks.
11. `ApiKeyController.Create`: validate requested scopes — reject anything not in
    the caller-issuable set (and keep the `operator:admin` emit-strip). This closes
    the self-grant gap for `tenant:provision` too.
12. Frontend `/api-keys`: let the developer pick capability scopes when creating a
    key (checkboxes for the dApp-dev scopes).

### Phase D — verification (single sweep at end, per working policy)
13. `dotnet build` + `dotnet test` (unit + integration).
14. SDK `tsc` + vitest. **Do not run frontend typecheck** (pre-existing noise).

---

## 4a. Hardening review outcomes (post-implementation)

A hardening review of the shipped diff found one CRITICAL and several minor issues:

- **C1 (CRITICAL, fixed)** — marketplace quest execution ran every node under the
  quest *owner's* identity, not the *runner's* (the engine ignored `run.AvatarId`
  and used `quest.AvatarId`). A non-owner starting a public quest could move the
  owner's assets / mutate the owner's holons, signed with the owner's key. Fixed by
  threading an explicit `ActingAvatarId` (= `run.AvatarId`) through the execution
  context, both executors (direct + durable), every side-effecting node handler, the
  binding resolver, and the capability gate. The owner path is unchanged (acting ==
  owner). Regression test added.
- **H1 (fixed with C1)** — binding resolver was owner-scoped; now scopes to the
  runner, so `$from: holon.<id>` resolves against the runner's holons.
- **M1 (fixed)** — avatar search matched on `Email`, an enumeration oracle even
  though email wasn't returned. Public search now matches username/title only.
- **M2 (fixed)** — cross-avatar holon clone copied all owner Metadata verbatim.
  Clone now omits `private_*` keys.
- **M3 (fixed)** — a legacy key whose non-empty CSV emitted zero scopes (all
  forbidden) was treated as full-access; now denied (only a genuinely-empty CSV is
  legacy full-access).

### Documented residual risks (L1/L2 — accepted, not code-changed)
- **L1 — logout-everywhere ~1s window.** `AuthNotBefore` is bumped to `UtcNow`, but
  the watermark check subtracts 1s to absorb JWT whole-second `iat` truncation (a
  requirement of the sovereignty claim-flow). Consequence: a token minted in the
  *same wall-clock second* as a "log out everywhere" call may retain sub-second
  residual validity. Acceptable for the threat model (device-loss / panic-button);
  tightening would require sub-second `iat` precision. **Do not rely on
  logout-everywhere for instantaneous revocation of a token issued that same second.**
- **L2 — public quest read exposes node configs.** A non-owner reading a public
  quest gets the full definition including node `Config`, which can embed the owner's
  holon IDs (via `$from: holon.<ownerHolonId>` bindings). Inherent to the readable-
  template marketplace model; holon IDs are identifiers, not secrets. If node configs
  later carry sensitive references, add a sanitized public quest projection (as was
  done for avatars). **Treat anything placed in a public quest's node config as
  publicly readable.**

## 5. Deferred (documented, not in this cut)
- **`dapp:{seriesId}:*` resource-scoping** — fence a key to one DappSeries. Needs a
  scoped-claim convention + a `DappSeriesId` linkage on fenced resources (holons
  carry only `AvatarId` today). Revisit if a developer needs to hand a key to a
  third party scoped to a single dApp.
- **Per-device session revocation** (jti table) — only if selective single-device
  logout becomes a real requirement.

---

## 6. Open questions for the owner
- **Q1 (avatar read)**: For `GET /avatar/{id}` of *another* avatar — 403, or a
  minimal public projection (username only, no email)? Does the frontend need to
  read other avatars at all?
- **Q2 (GetAll)**: Is `GET /api/avatar` used anywhere by the frontend, or safe to
  remove / lock to Operator?
- **Q3 (capability scope names)**: `holon:edit` / `quest:edit` / `dappseries:edit`,
  or a coarser single `dapp:develop` that covers all three?
- **Q4 (legacy keys)**: Confirm keeping "empty CSV = full access" is desired (it
  means capability scopes only ever *restrict*, never *grant* — the safe default).
