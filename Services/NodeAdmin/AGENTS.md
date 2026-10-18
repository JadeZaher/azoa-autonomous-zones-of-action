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
  only after a valid signature means unauthenticated traffic never writes. Markers are swept after `exp`; a
  later replay is already refused by the expiry check. Every failure is the uniform `AUTH_FAILED` (M4).
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
15. Unknown-challenge / malformed / unknown-enrollment failures are not individual audit rows; they appear in
    `auth.anonymous_failures` roll-ups (actor `anonymous`, target `node`).
