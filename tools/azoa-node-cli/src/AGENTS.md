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
once, then retry the original request once. See `commands/api.ts`.

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
