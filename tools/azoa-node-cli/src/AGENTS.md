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
