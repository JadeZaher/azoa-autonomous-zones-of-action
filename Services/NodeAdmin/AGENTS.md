---
type: doc
scope: Services/NodeAdmin
track: node-admin-rbac-cli_20261018
---

# Services/NodeAdmin — named node admins, break-glass root, RBAC, audit

Lanes A1 (plan P1 + P3: identity, auth, sessions, RBAC core) and A2 (plan P2: §operator-actor,
§route-permission-map, §reserved-id-inventory) of `conductor/tracks/node-admin-rbac-cli_20261018`. The wire contract is plan §3 as
amended by §8; §8 wins on conflict. Lane B (`tools/azoa-node-cli`) builds against the same contract.

Node admins are **not** platform users: separate tables (`node_admin*`), separate token kind
(`token_use=node_admin`), separate controllers (`api/node-admin/*`). A node-admin token can never act as a
user and a user/API-key/operator token can never satisfy a `NodeAdmin:*` policy. The legacy reserved node
operator (`Services/Admin`) keeps working unchanged; the existing operator policies accept either principal
(§route-permission-map).

## §configuration

| Key | Meaning |
|---|---|
| `NodeAdmin__RootPublicKeys` | comma list of base64url raw Ed25519 public keys (32 bytes, canonical, unpadded) |
| `NodeAdmin__RootKeyRevision` | monotonic; must be ≥ 1 when keys are set |
| `NodeAdmin__Audience` | the node's canonical origin (`https://host[:port]`, no path/trailing slash); default `PUBLIC_ORIGIN` |
| `NodeAdmin__SessionMinutes` | default 15, clamped 5–30 |
| `NodeAdmin__EnrollmentMinutes` | default 240, clamped 10–240 (H3: ≤ 4 h) |
| `NodeAdmin__ChallengeKey` | optional dedicated secret for the challenge MAC; unset → key = HKDF-SHA256(`Jwt:Key`, info `azoa-node-admin-challenge-v1`). Rotating either invalidates challenges in flight (≤ 60 s) |

`NodeAdminRuntime` validates once at boot and **never refuses boot** (M5). A malformed key, a missing
revision, or an env revision lower than the durable `node_admin_root_state.root_key_revision` disables root
login for the life of the process and logs `LogCritical`. A missing/invalid audience disables every
node-admin login (`NODE_ADMIN_UNAVAILABLE`, 503). Raising the revision advances the watermark and bumps
`root_session_revision`, ending every root session (audited as `root.revision_advanced`, actor `system`).

## §storage

`SurrealNodeAdminStore` (typed `SurrealQuery<T>`/`SurrealWriter` only). It lives here rather than in
`Providers/Stores/Surreal` because its one transaction composer (`CommitAsync`) needs `BEGIN`/`COMMIT`
literals, which the per-file raw-query ceiling there would otherwise have to be raised for.

- `CommitAsync(expectedDirectoryRevision, mutations, deletes)` wraps typed conditional mutations in one
  transaction; every mutation is captured with `SurrealGeneratedStatementScope.Assign` and must return a row
  (`RequireResult` → `NODE_ADMIN_CAS`). With a directory revision it first CASes `node_admin_directory`, so
  invariant checks computed from a snapshot at revision *r* commit only if nothing changed (M1). Callers
  retry up to 5 times on `Conflict` and then return `CONFLICT`.
- Record ids: admin = Guid `N` (wire `D`); credential = fingerprint (a key belongs to one admin forever;
  a revoked key is only reactivated for the same admin); session = **sha256 hex of the raw 16-byte sid** (M-1:
  only the token carries the sid, so a database read, a backup or an auditor's listing never yields a usable
  session id); sessions are listed and revoked by `handle`, an independent random 128-bit hex id that cannot be
  turned into a sid;
  audit = seq zero-padded to 20 digits; singletons `local`/`head`.
- FK-like columns (`admin_id`) are plain strings, not `record<>` links: the tables are self-contained and the
  bare-id rule therefore does not apply.
- New tables need no compatibility baseline (`Persistence/SurrealDb/AGENTS.md` §compatibility-baselines lists
  only pre-existing tables); plain `[Default]` is allowed because the tables are created in this change.

## §permissions

`Core/NodeAdminPermissions.cs` is the closed catalog (plan §2) and the five built-in roles. Built-ins are
seeded idempotently at boot by `NodeAdminBootstrapService` (code is the source of truth: a changed built-in
definition is written back under a directory CAS) and are immutable through the API (`ROLE_BUILT_IN`).
Root holds exactly `admins.*`, `roles.*`, `audit.read` (M3): break-glass bootstraps people, it never moves
money, decides KYC or governs.

Effective permissions are computed **per request** (C2) by `NodeAdminPermissionResolver` from the admin row
and its roles; tokens carry no permission claims. The role snapshot is cached per directory revision, and
every role write and every status/role assignment bumps that revision.

## §auth

`Managers/NodeAdminAuthManager`.

- `POST auth/challenge` is **stateless** (NEW-1, replacing the 10k outstanding-challenge cap, which was itself
  a root-login DoS): `challengeId = base64url(payload ‖ HMAC-SHA256(key, payload))`, payload =
  `{v, id (16 random bytes, hex), p (purpose), fp, aud, n (nonce), exp (exact expiresAt string)}` as JSON
  (`NodeAdminChallengeCodec`). Nothing is written, so issuance cannot fill the database or be capped into a
  denial of service; the `operator-login` limiter (IPv6 by /64) still bounds it. Issued for any well-formed
  fingerprint (no key-existence oracle). The client signs the returned strings byte-for-byte (H2); the server
  rebuilds the message from the payload's exact strings, `challengeId` included.
- `POST auth/session`: constant-time MAC check → expiry → audience → fingerprint(publicKey) = payload `fp` →
  Ed25519 verify → **then** CREATE a consumed marker in `node_admin_challenge` (record id = payload id; a
  collision is a replay → `AUTH_FAILED`, chained as `challenge_replay`) → root membership (full 32-byte
  fixed-time compare + root watermark) or credential (full key compare) + active status + TOTP. Recording use
  only after a valid signature means unauthenticated traffic never writes. A challenge whose `exp − now` exceeds the
  60 s lifetime + 30 s skew is refused (`challenge_lifetime_exceeded`, rolled up): no clock of this node issued it.
  Markers are swept only once `exp` is 5 minutes past (`ConsumedMarkerSkewMargin`, a clock-skew margin between the
  expiry check and the sweep); a later replay is already refused by the expiry check. Every failure is the uniform `AUTH_FAILED` (M4).
- Lockout (H-1, L-4): 5 consecutive **second-factor** failures lock the admin (`ADMIN_LOCKED` + `Retry-After`,
  only reported after a valid signature). Each failure is counted by a CAS retry loop that re-reads the row
  until its increment lands, so concurrent bad codes cannot lose counts; a racer that re-reads a locked row
  stops counting. The lock lasts 15 min, doubling per consecutive lock (`lock_count`), capped at 24 h; a
  successful login resets `lock_count`. The success write CASes on `(admin_revision, totp_last_step,
  lock_count)`, so a lock set after its read (which bumps `lock_count`) can never be cleared by it. `auth.lock`
  rows carry `lockCount` and `lockedForSeconds`. Signature failures are not counted: anyone can name a
  fingerprint, so counting them would let an attacker lock any admin out; signature guessing is infeasible and
  IP-limited by the `operator-login` limiter instead. The enrollment-completion counter uses the same loop.
  The success write also requires `failed_attempts` unchanged (L-a): any failure counted between its read
  and its write voids the success. A second or later consecutive lock (`lock_count ≥ 2`) logs a critical
  `NodeAdminRepeatedLock` event and sets `repeated: true` on the `auth.lock` row (L-d).
  Test note: the interleaving these predicates guard (a failure or lock landing between a success's read and
  write) is not deterministically reachable through HTTP without a test seam inside the manager; the 10-way
  concurrent lockout flow test exercises it probabilistically, so removing `lock_count ==` or
  `failed_attempts ==` from the success predicate is not guaranteed to turn a test red. Adding a seam was
  declined to keep production code free of test hooks.
- A TOTP ciphertext that cannot be decrypted **or decoded** (L-1) fails the login/enrollment uniformly and is
  audited as `totp.secret_unreadable`.
- TOTP: RFC 6238 SHA1/6/30, ±1 step, step must exceed `totp_last_step`, then a CAS advances it (replay
  guard; a concurrent second use of the same code loses the CAS). Secrets are DataProtection-encrypted with a
  purpose bound to the admin id (`NodeAdminTotpProtector`), so a ciphertext cannot be moved between admins.
- The success audit row is written **before** the session row and token exist; if the audit is down the
  login returns `AUDIT_UNAVAILABLE` and no session is issued.
- Enrollment (H3): `POST /admins` returns the one-time token to its creator only (hash stored);
  `enroll/start` binds exactly one key and a pending TOTP secret (`started_at` CAS; a second start is
  `ENROLLMENT_ALREADY_STARTED`, audited); `enroll/complete` proves the key (signature over the enroll
  message) and the authenticator, consumes the enrollment and moves the admin to `pending_activation`;
  `POST /admins/{id}/activate {fingerprint}` by root or a dominating admin confirms the fingerprint out of
  band. 5 failed completions invalidate the enrollment. All `/enroll/*` failures except already-started are
  the uniform `ENROLLMENT_INVALID`.

## §token-validation

`NodeAdminTokenValidation.ValidateAsync` is called from the **first** statement of `OnTokenValidated`
(`Program.cs`) and always ends in fail-or-return (H6), so a `root:<fp>` subject (not a Guid) can never reach
the user branch's "no resolvable subject" early return. It fails the token unless the routed endpoint carries
a `NodeAdmin:*` policy or is an operator route mapped to a permission (§route-permission-map), then requires a live session
row whose principal equals `sub`, and either an active admin whose `admin_revision`/`session_revision`
equal the token's, or a still-configured root key whose `root_key_revision`/`root_session_revision` match
both env and durable state. The validated `NodeAdminCaller` (with live permissions) is attached to
`HttpContext.Items`.

`NodeAdminAuthorizationPolicyProvider` resolves `NodeAdmin:<permission>[:recent]` and `NodeAdmin:session`
on demand (unknown permission → no policy → request fails closed). `:recent` = authenticated ≤ 10 min ago
(session `auth_time`, same window as `RecentNodeOperatorSession`); failing it yields 403
`STEP_UP_REQUIRED` + `x-azoa-auth-requirement: recent-node-admin-login`.

## §invariants

`Managers/NodeAdminManager` + pure `NodeAdminInvariants` (unit-tested). Root is exempt from dominance and
escalation (it can always mint a node-owner) but **not** from last-owner or self rules.

- **C1 dominance**: suspend/reactivate/revoke/activate/roles/revoke-sessions/key revoke of another admin
  require `perms(target roles) ⊆ perms(actor)`. MFA reset and key add for another admin additionally require
  an owner-equivalent actor (holds every permission). Editing a role to remove permissions requires
  dominance over every non-revoked holder of that role (it demotes them).
- **No escalation**: created/assigned/edited role permissions ⊆ actor permissions.
- **Last owner (M1)**: a change that takes the number of active admins holding both `admins.manage` and
  `roles.manage` from ≥ 1 to 0 is `LAST_OWNER`, checked against the snapshot whose directory revision the
  write CASes.
- **Self-protection**: no self suspend/revoke/MFA reset, no stripping your own `admins.manage` (by
  assignment or role edit), no revoking your only key.
- **Pending role change (M-2)**: changing the roles of a `pending_enrollment`/`pending_activation` admin
  sends it back to `pending_enrollment`, consumes every open enrollment and revokes any bound-but-unactivated
  key (audited `admin.reenrollment_required`), so a token issued under the old roles cannot enroll the raised
  admin; only a root caller receives the fresh token.
- **Root revocation (L-2, NEW-2)**: `POST /root/revoke-sessions` is **root-only** and bumps
  `root_session_revision`; an admin revoking any `root:*` session by handle is also `PERMISSION_DENIED`
  (audited as `denied`). Otherwise a rogue owner could keep break-glass root permanently signed out. Admins who
  must cut root off do it out of band: remove the key from `NodeAdmin__RootPublicKeys` and/or raise
  `NodeAdmin__RootKeyRevision` in the node environment and redeploy (every root session ends).
- **MFA reset** returns the admin to `pending_enrollment`, revokes every key, session and open enrollment;
  only a **root** caller receives the new enrollment token (C1), so a peer owner cannot re-enroll someone
  else's identity with their own key.
- Every status/role/credential change bumps `admin_revision`, which ends the target's sessions (tokens carry
  it). Permission changes through role edits apply live without ending sessions.

## §audit

`NodeAdminAuditLog`. Canonical hash (plan §8, the CLI recomputes it):
`hex(sha256(utf8(prevHash \n seq \n at \n actor \n action \n target \n outcome \n detail)))`, genesis
`prevHash` = 64 zeros; `amr`, `ip`, `requestId` are metadata, not hashed. Append = one transaction that CASes
the `node_admin_audit_head` singleton (seq + hash) and creates the row (record id and UNIQUE index on seq);
a loser re-reads the head and retries (16 attempts). Every field is `READONLY`; no update/delete path exists.
Each row is mirrored as a structured `NodeAdminAudit` log line (H5), without `detail`.

Coverage:
- `NodeAdminAuditActionFilter` (global MVC filter): for every non-GET request by a validated node-admin
  caller, an `intent` row **before** the action (failure → 503 `AUDIT_UNAVAILABLE`, action not run) and an
  `ok | denied | failed` row after, with `{status, code}` detail. Action = `VERB /route-template`, target =
  route values.
- `NodeAdminDenial` (from `ActionableAuthorizationResultHandler`): every 403 for a node-admin principal is
  an `authz.denied` row.
- The filter is ordered at -3000, before `ModelStateInvalidFilter` (-2000), so automatic invalid-body 400s on
  node-admin writes also get intent + `failed` rows (L-3).
- Inline: `auth.session` (ok/failed + reason), `auth.lock`, `totp.secret_unreadable`, `enroll.start`/
  `enroll.complete` (intent/ok/failed/denied), `admin.reenrollment_required`, `root.revision_advanced`.
- Flood control (M-3): failures tied to **no** real challenge, enrollment or admin (malformed bodies, unknown
  challenge ids, unknown enrollment tokens/ids) are not chained. `NodeAdminAnonymousFailures` logs a
  `NodeAdminAnonymousFailure` line and counts them; `NodeAdminAnonymousFailureFlusher` writes one
  `auth.anonymous_failures {count, reasons}` row per 5-minute window (and on shutdown), restoring the counts if
  the write fails. With stateless challenges anyone can mint an authentic challenge, so every refusal before the
  signature check (bad MAC, expired, audience, fingerprint mismatch) is rolled up, and a bad signature is
  chained only when its fingerprint names a configured root key or a registered credential (NEW-3; looked up
  only after verification failed). Replays, TOTP failures and enrollment failures on real enrollments stay
  chained individually.
- `GET /audit/verify` verifies `fromSeq..head` (default the last 10 000 rows), anchored on row `fromSeq-1`
  whose own hash is recomputed; `?full=true` rescans from genesis and is throttled to once per minute per
  principal (`RATE_LIMITED`, 429; L-b). The CLI is expected to run a periodic full verify and pin the last
  verified `(seq, hash)` (L-c, H5); the server keeps no schedule of its own. The hourly sweep loops 500-row batches until a short batch (≤ 50 per table).
- Never logged or audited: signatures, TOTP codes or secrets, tokens, enrollment tokens.

Exception boundaries: audit writes are the only place this module catches broad exceptions, and each catch
translates rather than swallows — an intent/success-path failure becomes 503 `AUDIT_UNAVAILABLE` (nothing
is changed or issued), a failure-path or outcome row is logged at Error so an audit outage cannot turn a
refused login into a 500 or undo a committed change. Workers (`NodeAdminBootstrapService` retry,
`NodeAdminSweepWorker`) catch to stay alive, rethrowing cancellation.

Honest scope (H5): the chain makes silent edits detectable (`GET /audit/verify`, CLI pinning of the last
verified head), not impossible — a database root can rewrite the whole chain. External WORM / signed
checkpoints are deferred.

## §threat-notes (plan §8)

- HS256 on the shared `Jwt:Key` (asymmetric signing deferred, plan §7): anyone holding `Jwt:Key` can mint a
  token, but a token is useless without a matching live session row and revision watermarks.
- Audience binding (H1): signatures name the node origin; the CLI refuses a challenge for another origin.
- Enumeration: challenge issuance and every login failure are uniform; `ADMIN_LOCKED` only after a valid
  signature.
- Sessions are server-side (C2): revoke = immediate, independent of token expiry.

## §operator-actor

Lane A2 (plan §4, §8 C3). `Core/OperatorActor.cs` is the one value for "who exercised node-operator authority":
`Legacy` (the reserved operator's password session), `Admin(id)` or `Root(fingerprint)`. `OperatorActorResolver`
(`HttpContext.TryGetOperatorActor`) resolves it once per request from the authenticated principal (validated
`NodeAdminCaller` → admin/root; legacy `node_operator` token for the reserved subject → legacy) and caches it in
`HttpContext.Items`.

**Acting-as model.** A named admin acts *as the node's operator subject*: `OperatorActor.SubjectUserId` is always the
reserved operator user, and every existing operator code path keeps receiving that Guid (wallet ownership, platform
genesis `genesis_user_id`, reviewer links, reserve authority). What changes is (1) the authority evidence and (2) the
recorded actor:

- Controllers on operator routes call `TryGetOperatorActor` instead of parsing `sub`, pass `SubjectUserId` where they
  passed the reserved id, and pass the actor to audit writers. Log lines name `AuditActor`.
- Domain audits gain `actor option<string>` (`legacy-operator` | `admin:<id>` | `root:<fp>`) beside their Guid
  `actor_user_id`, never a faked Guid: `kyc_control_audit`, `node_governance_audit`, `node_fee_audit`,
  `node_treasury_audit`, `action_recovery_audit` (baselined at their pre-change goldens + forward migration
  `20261018_120000`, Persistence/SurrealDb/AGENTS.md). Their read DTOs expose `actor`. Webhook registration/event-retry
  audits already store the full witness in `authority_json`, which now carries the node-admin evidence. Every other
  operator write (KYC decisions, AML decisions, recovery, payouts …) is attributed by the structural
  `NodeAdminAuditActionFilter` row (`VERB /route-template`, actor, outcome); AML decisions also log the actor.
  `deployment_recovery_audit.actor_user_id` is the deployment's owner, not an operator, and is unchanged.
- **Credential witness.** `SigningAdmissionAuthorityFactory.CaptureCredentialAsync` gives a node-admin principal a
  `Jwt` witness with `ActorUserId = SubjectUserId`, `TokenUse = node_admin`, `Scopes = [operator:admin]` (the policy
  already proved the mapped permission on this request) and `NodeAdmin = NodeAdminAuthorityWitness(principal,
  session record id, admin/session revisions | root key/session revisions)`; `IssuedAt` is the session `auth_time`
  (`ValidatedTokenTime`), so the 5-minute webhook and 10-minute genesis windows mean the same thing as for the legacy
  operator. `NodeAdmin` is `JsonIgnore(WhenWritingNull)`: legacy witness JSON and its hashes are byte-identical.
- `OperatorActor.IsOperatorCredential` replaces `TokenUse == node_operator` at every operator-witness site (genesis
  identity/fence/minter, webhook management and stores).
- **Fences mirror the session validator in the transaction.** `SigningAuthorityFence.Credential` keeps the legacy branch
  (reserved user row + `admin_bootstrap_state` revisions) and, for a node-admin witness, locks the subject user row
  (active; its login watermark is *not* applied to the admin's token) plus `node_admin_session` (record id, principal,
  `revoked_at = NONE`, `expires_at > now`) and either `node_admin` (`status = Active`, `admin_revision`,
  `session_revision`) or `node_admin_root_state` (`root_key_revision`, `root_session_revision`). Any change since
  capture (revoke, suspend, role/status change bumping `admin_revision`, root revision advance) throws
  `SIGNING_AUTHORITY_CHANGED`. `PlatformGenesisFence` inherits it through `ForRunAdmission`. Not re-checked in the
  transaction: the live permission set (role edits apply without bumping `admin_revision`, §invariants) and the env
  root-key list — both were checked on the same request moments earlier, the same freshness the legacy fence has.
  Statement params share one namespace per transaction, which is why the watermark cutoff switch is its own param
  (`_cutoff_applies`); `NodeAdminOperatorFenceTests` runs the fence against a real database.

## §route-permission-map

`NodeAdminRoutePermissions.Map` is the single table `VERB normalized-template → NodeAdminRouteGrant(permission,
requireRecent)` for every route whose policies include `Operator`, `NodeGovern`, `NodeOperatorSession`,
`RecentNodeOperatorSession` or `DappRoleAssignment` (115 rows). Assignment follows plan §2: reads →
`*.read`/`audit.read`, writes → the domain permission; recovery surfaces (and their reads, the catalog has no
`recovery.read`) → `recovery.operate`; webhook registration → `catalog.manage`; webhook event retry → `recovery.operate`;
fiat/treasury/platform-token reads → `money.read`; genesis/creation mutations → `platform.genesis`. `requireRecent` is
true exactly where the route carries `RecentNodeOperatorSession` or `DappRoleAssignment` (legacy step-up), so step-up is
the same 10-minute `auth_time` window for both principals. Two rows carry `null` (legacy only): the legacy password
login and its session revoke.

Gates (`tests/.../Architecture/NodeAdminRoutePermissionGateTests`): every live operator-policy route is mapped, no row is
stale, `requireRecent` equals the live recent-policy set, every permission is in the catalog, and every
`Inventories/RouteAuthorization/*.json` row with an operator policy is mapped. An unmapped route fails CI.

**Dual-accepting policies** (`Program.cs`, edited in place): each of the five operator policies first calls
`NodeAdminOperatorAccess.Admit(ctx)`; it returns `null` for any non-node-admin principal, so the legacy predicate runs
unchanged, and `true`/`false` for a node-admin principal: admitted only when the validated caller's live permissions
contain the mapped permission (and the session is recent where mapped). Denials record `PERMISSION_DENIED` or
`STEP_UP_REQUIRED` as failure reasons, so `NodeAdminDenial` writes the contract 403 (+ `x-azoa-auth-requirement:
recent-node-admin-login`) and an `authz.denied` audit row. Fail closed (M7): an unmapped or legacy-only operator route
denies every node admin. `EndpointAcceptsNodeAdmin` admits a node-admin token only on `NodeAdmin:*` endpoints and mapped
(non-null) operator routes, so elsewhere the token fails at validation (401). Root holds only `admins.*`, `roles.*`,
`audit.read` (M3): it can read domain audits but never satisfies a money/KYC/governance/platform/recovery grant.
`FirstPartyLogin` never treats a node admin as a first-party user; like the legacy operator, a node admin passes it only
on a route that also carries an operator policy (today only `POST api/apikey/tenant`), where that policy decides.
`DappRoleAssignment`'s user path (`CanAssignDappRoles`) is untouched; `UserController.ActingIsOperator` treats a
resolved node-admin actor as the operator (it reached the route holding `users.roles.assign`).

## §reserved-id-inventory

Plan §8 C3 (lane A2). Every production line matching `NodeOperatorIdentity.UserId`,
`NodeOperatorIdentity.ReservedEmail` or `IsNodeOperator` (comment lines excluded; `tests/`, `tools/`,
`frontend/`, `sdk/` excluded), counted per file and tagged:

- `actor-aware` — the site accepts an `OperatorActor` (legacy operator, named admin or root) or sits in a
  dual-accepting operator policy; a node admin reaches it only through a mapped route (§route-permission-map).
- `legacy-only` — the site deliberately keeps the reserved subject out of, or describes, something a node admin
  never acts through (user API exclusions, legacy login/session, tenant/subject exclusions, routes whose
  policies are not operator policies so node-admin tokens are refused at token validation).

`tests/AZOA.WebAPI.Tests/Architecture/ReservedIdentityInventoryTests` scans the tree and fails when a file's
matching-line count differs from the sum of its rows here: a new reserved-id comparison must be added to this
table with a tag and a reason.

| File | Sites | Tag | Reason |
|---|---|---|---|
| Controllers/ApiKeyController.cs | 1 | legacy-only | target guard: the reserved user can never own a tenant key |
| Controllers/HoldCommandsController.cs | 1 | legacy-only | reserve-asset listing on a plain `[Authorize]` route; node-admin tokens are refused there |
| Controllers/PlatformReserveController.cs | 1 | legacy-only | mixed operator/tenant declaration lane on a non-operator-policy route |
| Controllers/TenantPriceScheduleController.cs | 1 | legacy-only | mixed operator/tenant price lane on a non-operator-policy route |
| Controllers/UserController.cs | 2 | actor-aware | `ActingIsOperator` (dapp-role assignment) accepts any resolved `OperatorActor` |
| Core/AzoaClaims.cs | 1 | legacy-only | definition of the legacy `node_operator` token predicate |
| Core/OperatorActor.cs | 1 | actor-aware | `SubjectUserId`: the one place every `OperatorActor` maps to the operator-subject user row |
| Core/Surreal/PlatformGenesisFence.cs | 1 | actor-aware | operator subject check; the credential may be a legacy or node-admin witness |
| Core/Surreal/SigningAuthorityFence.cs | 1 | actor-aware | legacy witness branch; node-admin witnesses lock their session/admin rows instead |
| Helpers/PlatformGenesisIdentity.cs | 1 | actor-aware | genesis acts as the operator subject for any `OperatorActor` |
| Helpers/TenantAuthority.cs | 1 | legacy-only | tenant exclusion: the reserved user is never a tenant |
| Managers/KycControlPlaneManager.cs | 4 | actor-aware | `IsNodeOperator(operatorUserId)` guard (definition + AML list/decide) receives `OperatorActor.SubjectUserId` |
| Managers/KycControlPlaneManager.cs | 4 | legacy-only | tenant/subject exclusions (tenant select, AML subject, audit filter) and the overview of the legacy identity |
| Managers/NodeOperatorManager.cs | 5 | legacy-only | legacy operator login and session issuance |
| Managers/UserManager.cs | 11 | legacy-only | keeps the reserved user out of the user API (register, get, list, update, delete, roles, email) |
| Managers/WalletAuthManager.cs | 3 | legacy-only | the reserved user can never bind or log in with a wallet |
| Program.cs | 7 | legacy-only | `OnTokenValidated` legacy operator branch (revisions, integrity, reserved-subject guard) |
| Program.cs | 8 | actor-aware | dual-accepting policies (`FirstPartyLogin` deferral, `NodeOperatorSession`, `RecentNodeOperatorSession`, `DappRoleAssignment`, `Operator`, `NodeGovern`): the node-admin branch runs first, the legacy predicate is unchanged |
| Providers/Stores/Surreal/SurrealAdminBootstrapStateStore.cs | 2 | legacy-only | legacy credential binding |
| Providers/Stores/Surreal/SurrealUserStore.cs | 13 | legacy-only | reserved-user write/read exclusions in the user store |
| Services/Admin/SeedAdminHostedService.cs | 9 | legacy-only | seeds and verifies the legacy reserved operator |
| Services/Auth/ActionableAuthorizationResultHandler.cs | 1 | legacy-only | legacy step-up response; node-admin denials are written by `NodeAdminDenial` first |
| Services/Auth/ApiKeyAuthenticationHandler.cs | 1 | legacy-only | the reserved user can never authenticate with an API key |
| Services/Kyc/KycProviderRegistry.cs | 1 | legacy-only | tenant exclusion |
| Services/Reserve/PlatformReserveService.cs | 1 | legacy-only | reserve declaration authority from the non-operator-policy reserve route |
| Services/Reserve/ReserveDrawAuthority.cs | 4 | legacy-only | reserve draw authority flag (non-operator-policy routes) |
| Services/NodeAdmin/OperatorActorResolver.cs | 1 | actor-aware | recognizes the legacy operator token when resolving the request's `OperatorActor` |
| Services/Reserve/ReserveFungibleTokenMinter.cs | 1 | actor-aware | platform genesis mint: operator subject check; the credential may be a legacy or node-admin witness |
| Services/Signing/SigningAdmissionAuthorityFactory.cs | 1 | actor-aware | legacy credential capture; node-admin principals take the node-admin witness branch |

## §deferred

- Known-key timing oracle (security spot-check LOW, c): after a failed signature, `/auth/session` looks the fingerprint
  up (configured root key or registered credential) to decide whether to chain the failure, so a bad signature for a
  known key does one more read than one for an unknown key. The response is uniform, the difference is a single
  indexed read, and the `operator-login` limiter bounds probing; constant-work lookups are deferred.
- Node-admin access to the non-operator-policy operator lanes (reserve declaration/draw, node-wide price schedule,
  hold reserve listing): their inline `IsNodeOperator` checks stay legacy-only (§reserved-id-inventory) and node-admin
  tokens are refused there at token validation. Opening them needs an operator policy + map row first.
- Web console / BFF and SDK for named admins (the legacy console keeps the legacy operator; `operator-bff.ts`
  accepts only `token_use=node_operator`).
- Four-eyes approval, web-console login for named admins, asymmetric JWT signing, hardware keys, fleet
  management (plan §7).
- `node_admin_challenge.purpose = enroll` exists in the closed set but enrollment proofs sign the
  enrollment id directly (contract), so no enroll challenges are issued.

## §contract-deviations (A1 → orchestrator, lane B)

1. `POST /enroll/start` also returns `audience` and `fingerprint` (additive).
2. `POST /admins/{id}/mfa/reset` returns `{ admin, enrollmentToken, enrollmentExpiresAt }`; the token fields
   are `null` unless the caller is root (C1 says tokens go only to root or the first creator).
3. `/enroll/complete` failures (bad signature, bad TOTP, unknown/expired id) all return
   `ENROLLMENT_INVALID`, not `AUTH_FAILED`; `/auth/session` failures return `AUTH_FAILED`.
4. Lockout counts second-factor failures only (see §auth).
5. Audit `outcome` adds `intent` to `ok | denied | failed` (H4 intent rows); actor `system` for boot events.
6. Unconfigured audience → 503 `NODE_ADMIN_UNAVAILABLE` (code not in the §8 list).
7. HTTP statuses: `AUTH_FAILED` 401, `ADMIN_LOCKED` 423 + `Retry-After`, permission family 403,
   `NOT_FOUND` 404, state conflicts (`LAST_OWNER`, `USERNAME_TAKEN`, `ROLE_IN_USE`, `ROLE_BUILT_IN`,
   `ENROLLMENT_ALREADY_STARTED`, `FINGERPRINT_MISMATCH`, `CONFLICT`) 409, `AUDIT_UNAVAILABLE` 503, others 400.
8. Wire ids: admin ids are Guid `D`; custom role ids are Guid `N`; built-in role ids equal their names.
9. `PUT /admins/{id}/roles` is gated by `roles.manage` (plan §2 "role CRUD + assignment").
10. MFA reset also revokes the admin's keys (re-enrollment re-proves key and authenticator; the same key may
    be re-bound).
11. **Sessions use `handle`, never `sid` (M-1, security review 2026-10-18).** `GET /sessions` returns
    `Session { handle, principal, authTime, amr, expiresAt, revokedAt?, ip, current }`; revoke is
    `POST /sessions/{handle}/revoke`; `POST /auth/session` returns `handle` instead of `sid`; `GET /me` returns
    the session with its `handle`. The raw sid exists only inside the access token.
12. `PUT /admins/{id}/roles` now returns `{ admin, enrollmentToken, enrollmentExpiresAt }` (the same shape as
    MFA reset); the token is non-null only when root changed the roles of a pending admin (M-2).
13. New `POST /root/revoke-sessions` (L-2) → `bool`.
14. `GET /audit/verify?fromSeq=&full=` returns `{ ok, fromSeq, verifiedThroughSeq, headHash, firstBrokenSeq? }`;
    `full=true` can answer 429 `RATE_LIMITED`.
16. **Challenge ids are opaque, not GUIDs (NEW-1).** Wire shape is unchanged (`challengeId`, `nonce`,
    `expiresAt`, `audience`), but `challengeId` is now a ~300-character base64url string and it is part of the
    signed message. Clients must treat it as an opaque string and sign it verbatim (they already must, H2).
17. `POST /root/revoke-sessions` is root-only; admins get `PERMISSION_DENIED` (NEW-2).
18. **Lane A2 (operator routes).** Node-admin tokens are accepted on the mapped operator routes (§route-permission-map)
    with the contract 403s (`PERMISSION_DENIED`, `STEP_UP_REQUIRED` + header); an unmapped or legacy-only operator
    route answers 401 at token validation. Response shapes of those routes are unchanged except the domain-audit DTOs
    (governance/fee/treasury/KYC control audit), which gain an additive `actor` string. The built-in `governance` and
    `operations` roles hold every `.read`, so they can read money/KYC listings; writes stay per permission.
15. Unknown-challenge / malformed / unknown-enrollment failures are not individual audit rows; they appear in
    `auth.anonymous_failures` roll-ups (actor `anonymous`, target `node`).
