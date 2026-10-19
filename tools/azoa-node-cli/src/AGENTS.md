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
`mutate.ts`'s `mutateWithStepUpRetry`, and every `*Recent`-gated mutating command — including
`commands/api.ts`, which used to duplicate this logic inline — goes through it. The re-login
triggered this way runs `LoginOptions.quiet: true`, which routes its status line to stderr
instead of stdout, so a `--json` caller's stdout still contains exactly one JSON object (the
actual command's result).

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

## Independent code review batch (H1-H3/M1-M6, commit 561adf31 -> next)

An independent reviewer of `561adf31` found real bugs the e2e-against-a-fake-server suite
couldn't see because it never exercises a live terminal, a real filesystem race, or a second
real origin. Load-bearing ones, beyond what's already covered elsewhere in this file:

- **H1**: a global `--node` flag could redirect an *authenticated* command to a different origin
  while still attaching the real bearer, and simultaneously controlled the audience the H1 check
  compared itself against. Removed entirely, not patched — `context.ts`'s `resolveProfile` has
  no override parameter, so there is nothing left to redirect. `http/client.ts` also now asserts
  origin equality immediately before attaching the bearer as an independent second check.
- **H2**: the Windows ACL "verify" was `output.includes(username)` against the raw `icacls`
  text — which also matches the file's own PATH if it happens to contain the username, so it
  verified nothing. `storage/permissions.ts` now has a pure `parseIcaclsAces`/`verifyAcl` that
  requires exactly one non-inherited Full Control ACE for an identity resolved via `whoami`/
  `whoami /user`, table-tested against literal `icacls` output samples (`test/permissions.test.ts`)
  instead of a live Windows ACL.
- **H3**: `keygen`/`keys add`/`enroll` now refuse to overwrite an existing key file without
  `--force` (`commands/keyGuards.ts`), with an extra typed-name confirmation for root keys. Every
  name that becomes a path segment goes through `validation.ts`'s `assertValidName`.
- **M1**: `storage/fsutil.ts`'s `writeJsonFileSecure` writes to a sibling temp file at
  `{ mode: 0o600, flag: "wx" }` and renames atomically, instead of a default-mode write followed
  by a later `chmod` (a real TOCTOU window on POSIX).
- **M2**: `audit/chain.ts`'s `verifyChain` now also asserts seq contiguity (`seq === prevSeq+1`),
  and `commands/audit.ts` cross-checks the local recompute against the server's own
  `/audit/verify` result, refuses to advance the pin on any disagreement, and requires the
  server's head to carry the SAME hash at an already-pinned seq (not just a non-regressed seq).
  The row-paging loop refuses to continue if a page doesn't advance the cursor.
- **M4**: `keys add` and `enroll` now write the (possibly encrypted) key file to disk BEFORE the
  network call that depends on it existing, retaining it if that call fails — so a crash
  between "server accepted the proof" and "we saved the key" can't lose the only copy of a
  private key the server now expects to see again. The recovery behavior is amended below.
- **M5**: `root env`'s railway path sets the revision bump (skip-deploys) before the key-list
  change that actually deploys, for a removal; it also now refuses a non-integer revision read
  back from Railway instead of letting `Number(...)`'s `NaN` propagate, and is a true no-op
  (zero `variable set` calls) when nothing changed.
- **LOW**: a wrong key passphrase now maps to `AUTH_FAILED`/exit 3 ("wrong passphrase or
  corrupted key file") instead of a bare AES-GCM auth-tag error at the default exit 5;
  `io/input.ts`'s hidden-prompt reader restores raw mode on Ctrl-C and processes pasted chunks
  character-by-character (a paste with its own trailing `\n` used to never match a whole-chunk
  equality check); `api.ts`'s `--data @file` no longer echoes file contents into a parse error;
  `api.ts` re-checks the path AFTER `URL` normalization so `/api/../x` can't escape the `/api/`
  prefix via dot-segments; dead code (`isRecentAuth`, `rootLoginMessage`/`adminLoginMessage`,
  `createOutputSink`) was deleted.

**Consciously deferred**: `StoredSession.authTime` is still the CLIENT's clock, not a value
decoded from the server's token (the server doesn't return `auth_time` in the JSON response —
only as a JWT claim inside `accessToken`, which would need a dedicated no-verify JWT-payload
decoder to read). Low value given the step-up window is already a forgiving 10 minutes; flagged
here rather than silently dropped.

## Join-branch fixes folded in (join-report.md, commits ca4d5f97/2df57848/1a117d9a)

A real live drive against the actual backend found and fixed three more bugs no test (not even
the fake-server e2e suite) could catch, because each needed something the fakes don't have: a
real non-TTY pipe, a real empty-body 4xx from middleware the fake server doesn't replicate, or
the real README bootstrap command. Cherry-picked by hand against this round's in-progress edits
to the same three files (`git cherry-pick` itself refused with a dirty tree; each was re-applied
manually with the diff as a guide) rather than via a mechanical merge:

- **`io/input.ts`**: `readSecret`/`readPipedStdin` used to slurp the ENTIRE non-TTY stream on
  their first call, starving every later prompt in the same invocation (`enroll` alone needs 4 in
  order). Replaced with a hand-rolled FIFO line reader shared by `readSecret`/`readVisible`/
  `readPipedStdin`. This round's own H2-adjacent `readLineHidden` changes (Ctrl-C raw-mode
  restore, per-character paste handling) are for the TTY-only path and are untouched by this —
  the two fixes are orthogonal. Because the FIFO reader now correctly serves sequential piped
  reads, `io/passphrase.ts`'s `promptNewPassphrase` dropped the `stdinConsumed` pre-check it had
  grown earlier in this same round: an exhausted pipe now resolves a read with `""` at EOF rather
  than hanging, which the existing "passphrase cannot be empty" check already turns into a clear,
  non-hanging error.
- **`http/client.ts`**: a bare (empty-body) 4xx — the shape the ASP.NET Core JWT bearer
  middleware itself emits for a rejected token, before any controller or AZOAResult envelope
  exists — collapsed into `NETWORK`(5) regardless of status. `STATUS_FALLBACK_CODE` now maps
  401/403/404/409/423/429 to the same code family the envelope path would have used.
- **`commands/rootEnv.ts`**: the README's own documented bootstrap command,
  `--current "" --current-revision 0`, was rejected outright (0 looked non-positive) and, even
  past that, would have rendered `NodeAdmin__RootPublicKeys` with no `NodeAdmin__RootKeyRevision`
  line at all (nothing bumps `nextRevision` on a pure addition) — which disables root login on
  boot. `assertValidRevision` now accepts 0 (the legitimate "nothing deployed yet" state), and
  the render path clamps to `>= 1` whenever the rendered key list is non-empty.

New coverage: `test/cliProcess.test.ts` (spawns the real built CLI with real piped multi-line
stdin), `test/httpClient.test.ts` (a bare `node:http` server emitting each un-enveloped status),
`test/rootEnv.test.ts` (the non-railway bootstrap path).

## Key registration durability

Persist keys before any request that can bind them, including enrollment start. A lost response can follow a
committed registration or completed enrollment, so every server-call failure retains the private file and emits
recovery guidance on stderr. The operator confirms the public fingerprint and enrollment state before retrying;
a fresh enrollment uses a new name. No automatic cleanup can decide whether the remote commit happened.
Overwriting an existing root key requires typing its name even when the requested replacement kind is `admin`.

## Session revocation and command parsing

All authenticated management writes require recent login, including session revocations. Logout first attempts
self-revocation. On step-up, it obtains the current original handle before reauthentication, revokes that handle
through the shared retry helper, then revokes any replacement login session. Local session data is cleared only
after success or an explicit initial `AUTH_FAILED` proving the original bearer is already invalid. Other failures
retain the current session and propagate an error. Retrying self-revoke directly after login would revoke only the
replacement and leak the original session.

Commander exit override and excess-argument rejection are configured before constructing children so nested
commands inherit them. Parser errors are caught at the outer boundary and exit 2; help exits 0. Diagnostics and
recovery warnings use stderr, preserving stdout for command JSON.

## Audit verification snapshot

The CLI first gets the server's verified cutoff `(seq, hash)`, then fetches and recomputes only through that sequence.
Later appends belong to the next verification. Exact local/server equality is required even for an empty window;
pin regression and same-sequence hash-change checks remain. Full verification also recomputes locally from genesis.
Tests append a valid row deterministically between the verify response and the first raw-row page.
