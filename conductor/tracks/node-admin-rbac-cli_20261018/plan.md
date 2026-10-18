---
type: track-plan
track: node-admin-rbac-cli_20261018
created: 2026-10-18
---

# Plan — node admin identity, RBAC, MFA, `azoa-node` CLI

Ground truth (explore report 2026-10-18): one reserved operator principal; HS256 JWT on the
shared `Jwt:Key`, 5–30 min, no refresh; `OnTokenValidated` (`Program.cs:216-315`) admits an
operator token only on endpoints carrying `NodeOperatorSession | RecentNodeOperatorSession |
DappRoleAssignment | Operator | NodeGovern`; revision watermarks on `admin_bootstrap_state`;
fences `Core/Surreal/SigningAuthorityFence.cs:85-117`, `PlatformGenesisFence.cs:24-27` compare
against the reserved id; no operator audit; Ed25519 verify in
`Services/Signing/WalletSignatureVerifier.cs`, challenge store pattern in
`SurrealWalletAuthChallengeStore`; DataProtection key ring available.

## 1. Model

Node admins are **not** platform users: separate tables, separate token kind
(`token_use=node_admin`). This keeps the user API and the dapp-RBAC untouched and makes a
node admin structurally unable to act as a user (and vice versa).

Tables (new, all `[Default]`-safe, no baseline needed):

- `node_admin` — `id`, `username` (unique, lowercase 3–64), `display_name`, `status`
  (`pending_enrollment | active | suspended | revoked`), `role_ids[]`, `admin_revision`
  (bumped on any role/status/credential change), `session_revision` (bumped on revoke),
  `totp_secret_protected` (DataProtection-encrypted, option), `totp_last_step` (replay guard),
  `created_by` (admin id or `root:<fingerprint>`), `created_at`, `updated_at`.
- `node_admin_credential` — `id`, `admin_id`, `kind` (`ed25519`), `public_key` (32-byte
  base64url), `fingerprint` (sha256 → first 16 hex), `label`, `created_at`, `revoked_at`.
  Unique index on `fingerprint`.
- `node_admin_role` — `id`, `name` (unique), `built_in` (bool), `permissions[]`, `revision`.
  Built-ins seeded idempotently at boot; built-ins are immutable.
- `node_admin_challenge` — `id`, `purpose` (`root_login | admin_login | enroll`), `nonce`
  (32 random bytes b64url), `subject` (fingerprint or enrollment id), `expires_at` (now+60 s),
  `consumed_at`. Single-use via typed CAS (`consumed_at = NONE` → set).
- `node_admin_enrollment` — `id`, `admin_id`, `token_hash` (SHA-256 of a 32-byte token),
  `expires_at` (≤ 24 h), `consumed_at`, `totp_secret_pending_protected`.
- `node_admin_audit` — append-only: `seq` (monotonic), `at`, `actor` (`admin:<id>` |
  `root:<fp>` | `legacy-operator` | `anonymous`), `amr[]`, `action`, `target`, `outcome`
  (`ok | denied | failed`), `detail` (JSON, no secrets), `ip`, `request_id`, `prev_hash`,
  `hash` = SHA-256(prev_hash ‖ canonical row). Write path: one raw atomic statement that
  reads the tail and appends (`// raw: hash-chain append`), with transient-conflict retry.

Root keys: `NodeAdmin__RootPublicKeys` (comma list of base64url Ed25519 public keys) +
`NodeAdmin__RootKeyRevision` (monotonic, like the operator credential revision; stored in a
`node_admin_root_state` singleton so a rollback is refused). Removing a key from env and
bumping the revision kills every root session.

## 2. Permission catalog and roles

Permissions (`Core/NodeAdminPermissions.cs`, string constants, closed set validated on write):

| Permission | Covers (existing routes today) |
|---|---|
| `admins.read` / `admins.manage` | new admin CRUD, keys, MFA reset, enrollment |
| `roles.read` / `roles.manage` | new role CRUD + assignment |
| `audit.read` | `node_admin_audit` + existing domain audits (kyc/governance/fee/treasury) |
| `governance.read` / `governance.write` | `NodeGovernanceController` (params, fee schedule, treasury) |
| `kyc.read` / `kyc.decide` / `kyc.configure` | NodeOperatorController KYC reads / decisions / provider+tenant config; AML reviews/decisions |
| `users.read` / `users.roles.assign` | `GET api/user`, `POST api/search/operator` / `PUT api/user/{id}/dapp-role` |
| `tenants.keys.mint` | `POST api/apikey/tenant` |
| `money.read` / `money.operate` | fiat payouts/reversals/on-ramp lots/fiat events/floats/project treasuries (reads / mutations) |
| `platform.genesis` | platform-token genesis + creation mutations |
| `recovery.operate` | sagas, action/deployment/blockchain-op/dex/wtt/ethereum/hosted-session recovery, reconcile sweep, backfill, retired rows, wallet-identity conflicts |
| `keys.rotate` | `api/admin/key-rotation/rotate` |
| `catalog.manage` | holon-types writes, webhook registration |

Built-in roles: **node-owner** (all), **governance** (governance.*, audit.read, *.read),
**compliance** (kyc.*, users.read, audit.read), **operations** (money.*, recovery.operate,
*.read), **auditor** (every `.read` + audit.read). Custom roles = any subset.

Invariants (enforced in `NodeAdminManager`, each with a flow test):
- No escalation: an actor can only create/assign roles whose permissions ⊆ actor's permissions.
- Last node-owner: an operation that would leave zero active admins holding `admins.manage`
  + `roles.manage` is refused (`LAST_OWNER`) — except root, which can always add a node-owner.
- Self-protection: an admin cannot suspend/revoke themselves or strip their own `admins.manage`.
- Root sessions carry only `admins.*`, `roles.*`, `audit.read` — root bootstraps people, it
  does not move money or decide KYC (break-glass ≠ god mode). Recorded as owner-reviewable.

## 3. Auth protocol (wire contract — backend and CLI build against this)

Base: `{node}/api/node-admin`. All bodies JSON, camelCase. Errors: `AZOAResult` envelope with
`code`.

1. `POST /auth/challenge` (anonymous, rate-limited `operator-login`)
   `{ "purpose": "root_login" | "admin_login", "fingerprint": "<16 hex>" }`
   → `{ "challengeId", "nonce", "expiresAt", "audience" }` where `audience` = the node's
   public origin (`PUBLIC_ORIGIN`/configured). Always returns a challenge (no key-existence
   oracle); verification fails later for unknown fingerprints.
2. Client signs the UTF-8 bytes of
   `azoa-node-admin/v1\n{purpose}\n{audience}\n{challengeId}\n{nonce}\n{expiresAt}` with
   Ed25519 (raw 64-byte signature, base64url).
3. `POST /auth/session` `{ "challengeId", "publicKey", "signature", "totp"? }`
   - consumes the challenge (single use, unexpired, purpose matches, fingerprint(publicKey)
     == challenge subject);
   - `root_login`: publicKey ∈ `NodeAdmin__RootPublicKeys`; no TOTP; amr `["ed25519","root"]`;
   - `admin_login`: credential active, admin `active`, TOTP required and valid (±1 step, step
     > `totp_last_step`, then CAS-advance `totp_last_step`); amr `["ed25519","totp"]`;
   → `{ "accessToken", "expiresAt", "admin": { "id", "username", "roles", "permissions" } }`.
   Every outcome audited (failures with reason code, never the signature).
4. Token: HS256 on `Jwt:Key`, lifetime `NodeAdmin:SessionMinutes` (default 15, clamp 5–30),
   claims `sub` (admin id or `root:<fp>`), `token_use=node_admin`, `admin_revision`,
   `session_revision` (or `root_key_revision`), `auth_time`, `amr`, `jti`, one `perm` claim per
   permission. Step-up = `auth_time` ≤ 10 min (same window as `RecentNodeOperatorSession`).
5. `POST /auth/revoke` (self) — bumps own `session_revision`. `POST /admins/{id}/revoke-sessions`
   (`admins.manage`).

Enrollment:
- `POST /admins` (`admins.manage`, step-up) `{ username, displayName, roleIds[] }` →
  `{ admin, enrollmentToken, enrollmentExpiresAt }` (token shown once, only hash stored).
- `POST /enroll/start` (anonymous) `{ enrollmentToken, publicKey, label }` → registers the
  pending credential, returns `{ enrollmentId, otpauthUri, totpSecretBase32 }` (shown once).
- `POST /enroll/complete` (anonymous) `{ enrollmentId, totp, signature }` where signature
  signs `azoa-node-admin/v1\nenroll\n{audience}\n{enrollmentId}` → admin `active`, token
  consumed. Proves possession of both the private key and the authenticator.

Admin/role/key/MFA management (all `api/node-admin/*`, writes require step-up):
`GET/POST /admins`, `GET /admins/{id}`, `POST /admins/{id}/suspend|reactivate|revoke`,
`PUT /admins/{id}/roles`, `POST /admins/{id}/keys` (add second key: requires signature by an
existing key of that admin or `admins.manage`), `DELETE /admins/{id}/keys/{fp}`,
`POST /admins/{id}/mfa/reset` (→ new enrollment token), `GET/POST /roles`, `PUT/DELETE
/roles/{id}`, `GET /audit?afterSeq=&limit=`, `GET /audit/verify` (recompute chain),
`GET /me`.

## 4. Authorization integration

- New `NodeAdminPermissionRequirement(permission, requireRecent)` + an
  `IAuthorizationPolicyProvider` resolving `NodeAdmin:<perm>` and `NodeAdmin:<perm>:recent`.
- `OnTokenValidated`: a `node_admin` token is validated against `node_admin` row (status
  active, both revisions) or root state (key still listed, root revision); rejected on any
  endpoint whose policies are not node-admin-aware (extend the allowlist mechanism; keep the
  reverse rule that user tokens never satisfy node-admin policies).
- **Existing operator policies become dual-accepting**: `Operator`, `NodeGovern`,
  `NodeOperatorSession`, `RecentNodeOperatorSession`, `DappRoleAssignment` accept either the
  legacy reserved-operator token (unchanged) or a node-admin token holding the permission the
  route maps to. Mapping lives in one table (route → permission) checked by a gate test
  against `tests/.../Architecture/Inventories/RouteAuthorization/*.json` so an unmapped
  operator route fails CI.
- Fences (`SigningAuthorityFence`, `PlatformGenesisFence`) and audit writers take an
  `OperatorActor` (legacy | admin id | root fp) instead of assuming the reserved id.
- Every permission-gated write emits a `node_admin_audit` row via an endpoint filter (actor,
  route template, outcome), so coverage is structural, not per-controller discipline.

## 5. CLI (`tools/azoa-node-cli`)

TypeScript, Node ≥ 20, zero native deps: `node:crypto` (Ed25519 `generateKeyPairSync`,
`sign`, scrypt + AES-256-GCM for passphrase-protected key files), `commander`, `qrcode-terminal`
(TOTP enrollment QR), built with tsup; bin `azoa-node`.

- Profiles in `~/.azoa/config.json` (`{ profiles: { name: { node, keyPath, kind } } }`); keys
  in `~/.azoa/keys/<name>.key` (PKCS8 PEM, optionally encrypted); sessions in
  `~/.azoa/sessions/<profile>.json`; all files 0600 (on Windows: icacls current-user only).
- Commands: `keygen [--name] [--passphrase]` (prints public key + fingerprint),
  `root env [--railway --project --service]` (prints `NodeAdmin__RootPublicKeys=…`; with
  `--railway` sets it via the Railway CLI, appending, and bumps `NodeAdmin__RootKeyRevision`
  only on removal), `login [--root] [--profile]` (challenge → sign → prompt TOTP),
  `logout`, `whoami`, `admins list|create|show|suspend|reactivate|revoke|roles|revoke-sessions`,
  `keys add|revoke`, `mfa reset`, `enroll <token>` (keygen if needed → start → show QR →
  prompt code → complete), `roles list|create|update|delete`, `audit tail [--follow]`,
  `audit verify`, `api <METHOD> <path> [--data]` (authenticated passthrough to any operator
  route, honoring step-up 403 `x-azoa-auth-requirement` by prompting re-login).
- `--json` on every command for scripting; non-zero exit codes per failure class.

## 6. Phases and lanes

| Phase | Lane | Content | Model |
|---|---|---|---|
| P0 | orchestrator | this plan + critic review; record verdict | — |
| P1 | **A (backend)** | tables + schema gen, root state, challenge store, Ed25519 auth, token issue/validate, admin CRUD + enrollment, audit hash chain, `/me` | opus executor |
| P2 | A | permission catalog, roles, policy provider, dual-accepting operator policies, route→permission map + gate test, actor-aware fences/audits, audit endpoint filter | opus executor |
| P3 | A | TOTP (RFC 6238 test vectors), enrollment completion, replay guard, MFA reset | (in A) |
| P4 | **B (CLI)** | full CLI against §3 contract; unit tests with real crypto; contract fixture tests | sonnet executor (parallel with A) |
| P5 | join | CLI ↔ API end-to-end against the test host or local stack; docs (`docs/NODE-HOST.md` §8.10, `deploy/railway/DEPLOY.md`, `tools/azoa-node-cli/README.md`); security review; merge; Railway env + live drill | orchestrator + security-reviewer |

Lane A owns: `Controllers/NodeAdmin*`, `Managers/NodeAdmin*`, `Services/NodeAdmin/**`,
`Persistence/SurrealDb/Models/NodeAdmin*`, generated schema/migrations, `Core/NodeAdmin*`,
`Program.cs` auth section (in place), fences, `tests/**/NodeAdmin*`, RouteAuthorization
inventories. Lane B owns: `tools/azoa-node-cli/**` only. Shared contract: §3 of this file —
any change is made by the orchestrator and messaged to both lanes.

Review gates: P1+P3 and P2 each get `/security-review`-grade adversarial review (auth +
crypto + secrets) before merge; CLI gets code review. One integrated test sweep at P5.

## 7. Deferred (designed for, not built here)

- Four-eyes approval (`node_admin_approval` table; policy flag `requiresApproval` on
  permissions; CLI `approvals list|approve`).
- Web console login for named admins (password + TOTP or WebAuthn) and retiring the legacy
  reserved operator.
- Asymmetric JWT signing (separate node-admin signing key / JWKS) — tokens still share
  `Jwt:Key` for now; documented risk.
- Hardware keys (FIDO2/YubiKey) as an additional credential kind.
- Multi-node/fleet management (one CLI profile per node works today).

## 8. Binding amendments after critic review (2026-10-18, verdict APPROVE WITH EDITS)

These **supersede** anything above that conflicts. Both lanes implement them.

**C1 dominance.** Any mutation targeting another admin (roles, suspend, revoke, revoke-sessions,
keys, MFA reset) requires `perms(target) ⊆ perms(actor)`. MFA reset and key add for *another*
admin require root or an actor holding every permission (node-owner-equivalent). A new
enrollment token is returned only to root, or to the creator of a `pending_enrollment` admin
(first enrollment only). Flow tests for each.

**C2 server-side sessions + live permissions.** New table `node_admin_session` (`sid` = 128-bit
random b64url, `principal` (`admin:<id>`|`root:<fp>`), `auth_time`, `amr[]`, `expires_at`,
`revoked_at`, `ip`, `user_agent`). Token carries `sid`; `OnTokenValidated` requires an unrevoked,
unexpired session row whose principal matches `sub`. Effective permissions are computed per
request from the admin row + roles (cache keyed on admin_revision + max role revision); no `perm`
claims in the token (the session response returns permissions for display). Endpoints:
`GET /sessions` (own; `admins.read` for others), `POST /sessions/{sid}/revoke`. Root sessions use
the same table; `POST /auth/revoke` revokes the caller's own `sid`.

**C3 reserved-id inventory.** P2 starts by committing `Services/NodeAdmin/AGENTS.md`
§reserved-id-inventory listing every production `NodeOperatorIdentity.UserId` /
`IsNodeOperator` site, each tagged `actor-aware` or `legacy-only` with reason. Introduce
`OperatorActor` (Legacy | Admin(id) | Root(fp)) resolved once from the principal; actor-aware
sites take it. Gate test: new reserved-id comparisons outside the allowlist fail.

**H1 audience.** CLI refuses a challenge whose `audience` ≠ the profile's configured node origin
(exact string). Server audience = `NodeAdmin:Audience` config, defaulting to `PUBLIC_ORIGIN`;
one canonical origin per node.

**H2 canonical signing.** Client signs server-returned strings **byte-for-byte** (never
re-serializes); server rebuilds from the stored strings. Server emits `expiresAt` as
`yyyy-MM-ddTHH:mm:ss.fffffffK` UTC (`+00:00`). Message = fields joined by `\n`, no trailing
newline, UTF-8. Golden vectors (RFC 8032 test-1 key); both lanes MUST have a test reproducing
these exact signatures:

```
privateSeed(hex) = 9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60
publicKey(b64url)= 11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo
fingerprint      = 21fe31dfa154a261   (sha256(raw 32-byte pubkey) -> first 16 hex)
root_login  msg  = "azoa-node-admin/v1\nroot_login\nhttps://node.example.test\n0b9e2f5a-6f0c-4e57-9a51-2b1f0e0c3d41\nq83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA\n2026-10-18T17:00:00.0000000+00:00"
            sig  = -NPZcjbxjAdPdW_KYSK6scxyFybe8EXi6VWz2clrUYPj43I-7dJJiYQTP8HFfpgixA1ChD2ymBji1Kl9ylA-Bw
admin_login msg  = "azoa-node-admin/v1\nadmin_login\nhttps://node.example.test\n7d4c1b2a-0e9f-4a8b-8c7d-6e5f4a3b2c1d\nAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\n2026-10-18T17:00:00.0000000+00:00"
            sig  = daj9AS74SQyY-krCU38k00lgAcQXJMMtfx8u5DaXnRzGupenpanbZXfLiYPBkApyK9yAo9IjF-6OXNXzH0mFAQ
enroll      msg  = "azoa-node-admin/v1\nenroll\nhttps://node.example.test\n3f2e1d0c-9b8a-4776-8554-433221100fed"
            sig  = 8iizKreiGkkhhsPN7BpwI2IOHPQercUI2Je_Xg6BGTV4uWbMnMpQ_LaYcIYKmSOSa_VGbQgHsdembgsGQkSHAA
```

`keys add` (own second key) = step-up session (`auth_time` ≤ 10 min) + the new key signs
`azoa-node-admin/v1\nadd_key\n{audience}\n{adminId}\n{newFingerprint}` (proof of possession).
No "signed by an existing key" path.

**H3 enrollment state machine.** `pending_enrollment` → `/enroll/start` binds exactly one key
(any later start → `ENROLLMENT_ALREADY_STARTED`, audited) → `/enroll/complete` (valid TOTP +
signature) → `pending_activation` → `POST /admins/{id}/activate { fingerprint }` by the creator,
a dominating admin, or root, confirming the fingerprint out of band → `active`. Enrollment token
TTL ≤ 4 h. CLI reads the token from a prompt/stdin, never argv.

**H4 audit.** Unique index on `seq`; singleton `node_admin_audit_head` (`seq`, `hash`) updated in
the same transaction as the append (serializes writers; transient-conflict retry re-reads the
head). Denials recorded from `ActionableAuthorizationResultHandler` for node-admin principals.
Coverage via a global MVC `IAsyncActionFilter`: `intent` row before a permission-gated write
(refuse with `AUDIT_UNAVAILABLE` if it fails), outcome row after. Login/enrollment audited inline.

**Audit hash (canonical, CLI recomputes it):** `hash = hex(sha256(utf8(prevHash + "\n" + seq +
"\n" + at + "\n" + actor + "\n" + action + "\n" + target + "\n" + outcome + "\n" + detail)))`,
where `at` and `detail` are the exact stored strings (`detail` is a JSON string stored verbatim,
`""` when empty), `seq` decimal, genesis `prevHash` = 64 × `0`. `amr`, `ip`, `requestId` are not
hashed (metadata).

**H5 tamper evidence (honest scope).** Each audit row is also emitted as a structured log line
(`NodeAdminAudit` event, no secrets). CLI `audit verify` recomputes the chain and pins the last
verified `(seq, hash)` per profile in `~/.azoa/`, alerting on regression. External WORM / signed
checkpoints deferred and documented.

**H6 OnTokenValidated ordering.** The `node_admin` branch runs **first** and always ends in
fail-or-return. Tests: a node-admin token is rejected on a plain `[Authorize]` user route; a
`root:<fp>` token never reaches the "no resolvable subject" early return.

**M1** last-owner check atomic with the write (CAS on a `node_admin_directory` revision
singleton) on assign, suspend, revoke, role edit, role delete. **M2** solved by C2. **M3** root
sessions never hold money/KYC/governance/platform/recovery permissions; root-created owners are
audit-flagged (`created_by=root:<fp>`). **M4** one uniform `AUTH_FAILED` for every
`/auth/session` and `/enroll/*` failure (reason only in audit); per-admin backoff (5 failures →
15 min lock, audited); compare the full 32-byte key. **M5** `node_admin_root_state` gains
`root_session_revision`; a lower env root revision *disables root login* (logged loudly) instead
of refusing boot. **M6** challenge CAS re-checks on retry; expired challenges/enrollments/sessions
swept hourly. **M7** unmapped operator route denies node admins at runtime (fail closed);
`CanAssignDappRoles` user path unchanged (regression test).

**CLI (binding):** scrypt N=2^17, r=8, p=1, maxmem 256 MiB; passphrase mandatory for root keys;
Windows: `icacls <path> /inheritance:r /grant:r "<user>:F"` and verify; warn if `~/.azoa` is
under OneDrive. `api` accepts only paths starting `/api/`, no absolute URLs, `redirect: "manual"`.
`root env --railway` shows a diff and requires `--yes`; removal bumps `NodeAdmin__RootKeyRevision`
in the same call. TOTP: SHA1, 6 digits, 30 s, URI
`otpauth://totp/AZOA%20Node%20({host}):{username}?secret=…&issuer=AZOA%20Node%20({host})&algorithm=SHA1&digits=6&period=30`.
`audit tail --follow` cut.

**Error codes** (`result.code`): `AUTH_FAILED`, `STEP_UP_REQUIRED` (403 + header
`x-azoa-auth-requirement: recent-node-admin-login`), `PERMISSION_DENIED`, `DOMINANCE_REQUIRED`,
`ESCALATION_REFUSED`, `LAST_OWNER`, `SELF_ACTION_REFUSED`, `ENROLLMENT_ALREADY_STARTED`,
`ENROLLMENT_INVALID` (uniform: expired/consumed/unknown), `FINGERPRINT_MISMATCH`, `ADMIN_LOCKED`
(only after successful signature, to avoid an oracle), `USERNAME_TAKEN`, `ROLE_IN_USE`,
`ROLE_BUILT_IN`, `UNKNOWN_PERMISSION`, `VALIDATION_FAILED`, `AUDIT_UNAVAILABLE` (503).

**Schemas** (camelCase): `Admin { id, username, displayName, status, roles: Role[], permissions:
string[], createdBy, createdAt, updatedAt, keys: Key[], mfaEnrolled }`; `Key { fingerprint, label,
createdAt, revokedAt? }`; `Role { id, name, builtIn, permissions, revision }`; `Session { sid,
principal, authTime, amr, expiresAt, revokedAt?, ip, current }`; `AuditRow { seq, at, actor, amr,
action, target, outcome, detail, requestId, prevHash, hash }`; `GET /audit?afterSeq&limit≤500 →
{ rows, head: { seq, hash } }`; `GET /audit/verify → { ok, verifiedThroughSeq, headHash,
firstBrokenSeq? }`; `GET /me → { principal, admin?, root?: { fingerprint }, permissions, session }`;
`POST /auth/session → { accessToken, expiresAt, sid, principal, permissions, admin? }`.
