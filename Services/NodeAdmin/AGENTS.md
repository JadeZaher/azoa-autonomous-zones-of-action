---
type: doc
scope: Services/NodeAdmin
track: node-admin-rbac-cli_20261018
---

# Services/NodeAdmin — named node admins, break-glass root, RBAC, audit

Lane A1 of `conductor/tracks/node-admin-rbac-cli_20261018` (plan P1 + P3). The wire contract is plan §3 as
amended by §8; §8 wins on conflict. Lane B (`tools/azoa-node-cli`) builds against the same contract.

Node admins are **not** platform users: separate tables (`node_admin*`), separate token kind
(`token_use=node_admin`), separate controllers (`api/node-admin/*`). A node-admin token can never act as a
user and a user/API-key/operator token can never satisfy a `NodeAdmin:*` policy. The legacy reserved node
operator (`Services/Admin`) is untouched.

## §configuration

| Key | Meaning |
|---|---|
| `NodeAdmin__RootPublicKeys` | comma list of base64url raw Ed25519 public keys (32 bytes, canonical, unpadded) |
| `NodeAdmin__RootKeyRevision` | monotonic; must be ≥ 1 when keys are set |
| `NodeAdmin__Audience` | the node's canonical origin (`https://host[:port]`, no path/trailing slash); default `PUBLIC_ORIGIN` |
| `NodeAdmin__SessionMinutes` | default 15, clamped 5–30 |
| `NodeAdmin__EnrollmentMinutes` | default 240, clamped 10–240 (H3: ≤ 4 h) |

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
  a revoked key is only reactivated for the same admin); session = hex of the 16-byte sid (wire base64url);
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

- `POST auth/challenge` always issues a 60 s challenge for a well-formed fingerprint (no key-existence
  oracle). `expires_at_text` and `audience` are stored verbatim; the server rebuilds the signed message from
  stored strings, the client signs returned strings byte-for-byte (H2).
- `POST auth/session` consumes the challenge by CAS **before** verifying anything, so a challenge is single
  use even on failure (M6), then checks fingerprint binding, audience, the Ed25519 signature, and either root
  membership (full 32-byte fixed-time compare against configured keys + root watermark) or the admin
  credential (full key compare) + active status + TOTP. Every failure is the uniform `AUTH_FAILED`; the
  reason is only in the audit row (M4).
- Lockout: 5 consecutive **second-factor** failures lock the admin for 15 min (`ADMIN_LOCKED`, only
  reported after a valid signature). Signature failures are not counted: anyone can name a fingerprint, so
  counting them would let an attacker lock any admin out; signature guessing is infeasible and IP-limited by
  the `operator-login` limiter instead.
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
a `NodeAdmin:*` policy (lane A2 extends this to the mapped operator routes), then requires a live session
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
- Inline: `auth.session` (ok/failed + reason), `auth.lock`, `enroll.start`/`enroll.complete`
  (intent/ok/failed/denied), `root.revision_advanced`.
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

## §reserved-id-inventory

Owed by lane A2 (plan §8 C3): list every `NodeOperatorIdentity.UserId` / `IsNodeOperator` site, tagged
`actor-aware` or `legacy-only`, and introduce `OperatorActor`. A1 touches none of them.

## §deferred

- P2 (lane A2): dual-accepting operator policies, route → permission map + gate test, actor-aware fences,
  extending `EndpointAcceptsNodeAdmin`.
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
