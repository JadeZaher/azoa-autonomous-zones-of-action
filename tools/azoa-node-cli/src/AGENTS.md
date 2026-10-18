---
type: directory-doc
---

# tools/azoa-node-cli/src — design notes

Lane B of `conductor/tracks/node-admin-rbac-cli_20261018`. Implements the CLI side of the
wire contract in `plan.md` §3 and the binding amendments in §8. Read those first — this file
only records the "why" behind choices the spec left to the implementer.

## Canonical signing (`crypto/canonical.ts`)

§8 H2 requires signing server-returned strings byte-for-byte, never re-serializing. The
message is `PROTOCOL_PREFIX + "\n" + fields.join("\n")` with no trailing newline. We never
reconstruct `expiresAt` ourselves — it is passed through verbatim from the server's challenge
response into the signed message, because the server's `yyyy-MM-ddTHH:mm:ss.fffffffK` format
does not round-trip through `Date` without losing precision.

Node's `generateKeyPairSync("ed25519")` PKCS8 DER export is the standard RFC 8410
`30 2e 02 01 00 30 05 06 03 2b 65 70 04 22 04 20 <32-byte seed>` wrapper — `crypto/keys.ts`
exploits this fixed 16-byte prefix to go seed ⇄ PKCS8 directly, which is what makes the §8
golden-vector test (RFC 8032 test-1 seed) a straight `crypto.sign(null, msg, privateKey)` call.
Verified against the plan's three golden signatures before writing any other code.

## Key file envelope (`crypto/passphrase.ts`, `storage/keyFile.ts`)

**CONTRACT ASSUMPTION:** plan.md §5 says keys are stored as "PKCS8 PEM, optionally encrypted".
The binding CLI security amendments mandate a specific KDF (scrypt N=2^17 r=8 p=1, 256 MiB) and
cipher (AES-256-GCM) that do not correspond to any OpenSSL-standard encrypted-PKCS8 PEM envelope
Node can emit natively. Key file format is local-only (never crosses the wire contract), so we
use a small JSON envelope at the same `~/.azoa/keys/<name>.key` path instead of literal PEM:
`{ version, kind, publicKey (b64url), fingerprint, encryption: null | { kdf, n, r, p, salt, iv, authTag }, privateKey (base64 PKCS8 DER, plaintext or ciphertext) }`.
The DER payload inside is still genuine PKCS8. Root keys always require `encryption`; admin
keys may opt out only via explicit `--no-passphrase`.

## Server-side session semantics the CLI relies on

The CLI never computes TOTP or recency windows itself — those are server-enforced (§3.4, §8 C2).
The CLI's only responsibility on `STEP_UP_REQUIRED` is: re-run the stored profile's login flow
once, then retry the original request once. This is implemented exactly once, in
`mutate.ts`'s `mutateWithStepUpRetry`, and every `*Recent`-gated mutating command goes through it
— not per-command logic. See `commands/api.ts` for the one caller that needs the raw
`StepUpRequiredError` directly (it validates the path/method first and wants to report the
absolute-URL guard before touching the network).

## Audit chain recompute (`audit/chain.ts`)

Implements the §8 H4/H5 formula verbatim:
`hash = hex(sha256(utf8(prevHash + "\n" + seq + "\n" + at + "\n" + actor + "\n" + action + "\n" + target + "\n" + outcome + "\n" + detail)))`.
`amr`, `ip`, `requestId` are metadata, never hashed. Genesis `prevHash` is 64 `"0"` chars.

## Cross-checked against lane A1 (backend, commit f4d5a77e)

Once the real backend landed, three response shapes and one query param turned out to differ
from the initial plan-only assumptions (see `Services/NodeAdmin/AGENTS.md`
§contract-deviations in that worktree):

- `/enroll/start` also returns `audience` + `fingerprint` — `commands/enroll.ts` treats both as
  an H1-style abort-before-continuing check, not just informational fields.
- `/admins/{id}/mfa/reset` returns `{ admin, enrollmentToken: string|null, enrollmentExpiresAt: string|null }`
  — the token is `null` unless the caller is root (C1: a peer owner can't re-enroll someone
  else's identity with their own key). `commands/mfa.ts` branches on that.
- `GET /sessions` filters by the full `principal` string (`admin:<id>`/`root:<fp>`), not a bare
  admin id — `commands/sessions.ts`'s `--admin` flag auto-prefixes a bare id with `admin:`.
- `PUT /roles/{id}` is a full replace sharing `NodeAdminRoleRequest` with create (`{name,
  permissions}`), so `roles update` requires `--name`, not just `--permissions`.
- Built-in role ids equal their names (`node-owner`, not a synthetic `role-node-owner`);
  `PUT /admins/{id}/roles` is gated by `roles.manage`, not `admins.manage`.

`errors.ts` gained three codes only visible once the backend's actual `StatusFor` switch could
be read: `NOT_FOUND`/`CONFLICT` (exit 2 — a wrong id or stale state is a user-input problem) and
`NODE_ADMIN_UNAVAILABLE` (exit 5 — the node has no `NodeAdmin__Audience` configured).
`ADMIN_LOCKED` carries `retryAfterSeconds` on the wire; the CLI formats it into the error message
rather than just naming the code.

## Two more backend rounds (commits 08384168, a90f6ab6)

- **Sessions use an opaque `handle`, never the real `sid`** (security review, M-1): `handle`
  replaced `sid` in every response (`auth/session`, `GET /sessions`, `GET /me`), and
  `POST /sessions/{handle}/revoke` is looked up by it. `StoredSession.handle` and
  `SessionRow.handle` throughout — the raw sid only ever lives inside `accessToken`, which the
  CLI already never printed.
- **`PUT /admins/{id}/roles` returns the same reenrollment shape as `mfa reset`** (M-2):
  `{ admin, enrollmentToken, enrollmentExpiresAt }`, non-null only when root changed a still-
  pending admin's roles (that reset forces re-enrollment). `commands/admins.ts`'s
  `runAdminsSetRoles` branches on it the same way `mfa.ts` does.
- **`POST /root/revoke-sessions`** (L-2, then tightened root-only in NEW-2): ends every root
  session; the CLI (`commands/rootRevokeSessions.ts`) checks the active profile's `kind` locally
  — before any network call — and refuses with a hint toward `root env --railway --remove <fp>`
  if it isn't `root`. `sessions revoke` of a `root:*` session's handle has the identical rule.
- **`GET /audit/verify?fromSeq=&full=`**: `fromSeq` defaults to the local pin (incremental);
  `--full` ignores it and asks the server to re-walk the whole chain from genesis, which can
  answer `RATE_LIMITED` (429) — a new exit-5 code with a formatted retry time, same machinery as
  `ADMIN_LOCKED`.
- **Challenge ids are opaque** (NEW-1): the server moved to stateless, HMAC-authenticated
  ~300-char `challengeId` strings instead of GUIDs. Nothing in the CLI needed to change — it
  always treated `challengeId` as an opaque string to sign verbatim — but `test/fixtures/fakeNodeServer.ts`
  now issues one too, and `test/e2e.test.ts` asserts on its shape, so a regression toward
  "validate it as a UUID" would be caught.
- **`ADMIN_LOCKED` backoff is exponential**: `formatRetryAfter` in `errors.ts` now renders hours
  (`"1h 30m"`) as well as minutes/seconds, since a repeated lockout can exceed 60 minutes.
