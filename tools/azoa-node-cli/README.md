# azoa-node CLI

CLI for Azoa node admin identity, RBAC, MFA and break-glass root bootstrap — the client side
of `conductor/tracks/node-admin-rbac-cli_20261018/plan.md` §3 (wire contract) and §8 (binding
amendments). Read that file for the authoritative protocol; this README is the operational
cheat sheet.

## Install / run

From the repo root:

```powershell
cd tools/azoa-node-cli
npm install
npm run build
node dist/cli.js --help
```

Or without a global install, via `npx` once published, or directly:

```powershell
npx tsx src/cli.ts --help          # dev, no build step
node dist/cli.js admins list --json
```

## Bootstrap walkthrough

1. **Generate a root key** (passphrase is mandatory for root keys):
   ```powershell
   azoa-node keygen --name root --kind root
   ```
   Prints the public key and its 16-hex-char fingerprint. The private key never leaves this
   machine and is never printed.

2. **Put the public key in the node's env** and deploy:
   ```powershell
   azoa-node profile add rootp --origin https://your-node.example.com --kind root --key-name root --use
   azoa-node root env --profile-keys rootp --current "" --current-revision 0
   # or, against a live Railway deployment:
   azoa-node root env --profile-keys rootp --railway --project <id> --environment <id> --service <id> --yes
   ```
   This renders (and, with `--railway --yes`, sets) `NodeAdmin__RootPublicKeys=...`. Redeploy the
   node so it picks up the new env value.

3. **Root logs in** (Ed25519 challenge, no TOTP):
   ```powershell
   azoa-node login --root --profile rootp
   ```

4. **Root creates the first node admin**:
   ```powershell
   azoa-node admins create --username alice --display-name "Alice" --role node-owner --profile rootp
   ```
   Prints an enrollment token **once** — hand it to Alice out of band (she never puts it on argv).

5. **Alice enrolls from her own machine** (never the root machine):
   ```powershell
   azoa-node enroll --origin https://your-node.example.com --profile-name alice
   ```
   Reads the enrollment token from a hidden prompt or piped stdin, generates her own keypair
   locally, shows a QR code + secret for her authenticator app, and signs a proof-of-possession
   message. The admin lands in `pending_activation` — not yet usable.

6. **Someone with `admins.manage` activates Alice**, confirming her fingerprint out of band
   (phone call, Slack DM she can be impersonated on is not "out of band"):
   ```powershell
   azoa-node admins activate <alice-admin-id> --fingerprint <printed-fingerprint> --profile rootp
   ```
   Now Alice can `azoa-node login --profile alice` with her key + TOTP code.

## Security notes

- **Keys**: `~/.azoa/keys/<name>.key` — a JSON envelope (not literal PEM; see
  `src/AGENTS.md` "Key file envelope" for why) carrying a PKCS8 DER Ed25519 private key,
  optionally encrypted with scrypt (N=2^17, r=8, p=1, 256 MiB) + AES-256-GCM. Root keys require
  a passphrase (min 12 chars, confirmed); admin keys may opt out with `--no-passphrase`.
  `AZOA_KEY_PASSPHRASE` is the only non-interactive way to supply one — never a CLI argument,
  where it would land in shell history and process listings.
- **No overwrite without saying so**: `keygen`/`keys add`/`enroll` all refuse to clobber an
  existing key file unless `--force`; `--force` on an existing **root** key additionally requires
  typing the key's name back to confirm (`src/commands/keyGuards.ts`). Every name that becomes a
  path component — key names, profile names — is restricted to `^[A-Za-z0-9_-]{1,64}$`
  (`src/validation.ts`), so neither a typo nor a hostile value can escape `~/.azoa/`.
- **File permissions, written correctly the first time**: every file under `~/.azoa/` is written
  to a sibling temp file at `{ mode: 0o600, flag: "wx" }` and renamed into place — never a
  default-mode write followed by a later `chmod`, which would leave a brief window where the data
  is readable by more than the owner (POSIX). `~/.azoa` subdirectories are created at `0o700`. On
  Windows, `icacls ... /inheritance:r /grant:r "<user>:F"` is followed by actually parsing the
  re-read ACL (`src/storage/permissions.ts`'s `parseIcaclsAces`/`verifyAcl`, table-tested against
  real `icacls` output) to confirm exactly one non-inherited Full Control entry for the current
  user — not a substring check against the raw `icacls` text, which would also match the file's
  own path if it happened to contain the username. A warning is printed if `~/.azoa/keys`
  resolves under a OneDrive-synced path.
- **Audience pinning (H1)**: the CLI refuses to sign anything if the server's challenge
  `audience` does not exactly match the profile's configured node origin. This is the one check
  standing between a real login and a MITM/relay replaying a challenge from a different node —
  and it only holds because that origin is **never overridable per-invocation**: there is no
  global `--node` flag (removed; see `src/context.ts`), so the value being compared against is
  always exactly what `profile add`/`enroll` pinned in `~/.azoa/config.json`. The HTTP client
  (`src/http/client.ts`) also asserts the resolved request origin matches its configured origin
  immediately before attaching the bearer, as a second, independent backstop.
- **Opaque challenge ids**: the server's `challengeId` is a ~300-character opaque string (a
  stateless HMAC-authenticated payload, not a GUID) and is itself part of the signed message. The
  CLI never parses, validates the shape of, or length-limits it — it is signed byte-for-byte like
  every other server-returned string (H2, `src/crypto/canonical.ts`).
- **Step-up**: every mutating command that sits behind a `*Recent` (step-up) policy — `keys
  add/revoke`, `admins create/suspend/reactivate/revoke/activate/roles/revoke-sessions`, `roles
  create/update/delete`, `mfa reset`, `root revoke-sessions`, and the generic `api` passthrough —
  goes through one seam (`src/mutate.ts`'s `mutateWithStepUpRetry`): try the call, and on
  `STEP_UP_REQUIRED` transparently re-run the stored profile's login flow once and retry the
  original request exactly once. Implemented once, not per command, so there's one place to
  audit; the transparent re-login's own status line goes to **stderr**, never stdout, so a
  `--json` caller's stdout still contains exactly one JSON object.
- **Audit pins**: `audit verify` recomputes the hash chain locally from `~/.azoa/audit-pins/<profile>.json`,
  cross-checks that recompute against the server's own `/audit/verify`, and alerts (exit code 6)
  on: a tampered row, a head that regressed behind what was previously pinned, a server head that
  reports a different hash at the already-pinned seq, or a disagreement between the local
  recompute and the server's verify — see `src/audit/chain.ts` and `src/commands/audit.ts`. Page
  fetching refuses to loop if a page ever fails to advance the cursor.
- **Root sessions can only be ended by root**: `root revoke-sessions` fails locally (exit 4,
  before any network call) if the active profile isn't `kind: root`; the server refuses it too,
  even for an admin holding every permission. To cut root off without a live root session,
  rotate `NodeAdmin__RootKeyRevision`/root keys via `root env --railway --remove <fp>` instead.
  The same rule applies to `sessions revoke` of a `root:*` session's handle.
- **A wrong key passphrase** fails with `AUTH_FAILED` / exit 3 ("wrong passphrase or corrupted
  key file"), not a bare AES-GCM auth-tag error collapsed into exit 5.
- **Un-enveloped 4xx responses** (e.g. the ASP.NET Core JWT bearer middleware's own empty-body
  401/403 for a revoked/malformed token, emitted before any controller or AZOAResult envelope
  exists) still map to the right exit-code family — 401→3, 403→4, 404/409→2, 423/429→3/5 with a
  retry time — rather than collapsing into a generic NETWORK(5) just because there was no `code`
  to read.
- `azoa-node` never reads or prints `.env` values, and never forwards its bearer token to any
  origin other than the profile's configured node.

## Command reference

| Command | Notes |
|---|---|
| `keygen --name <n> [--kind root\|admin] [--no-passphrase] [--force]` | Root keys always require a passphrase. `--force` to overwrite an existing key of the same name; overwriting a root key additionally requires typing its name to confirm. |
| `root env [--profile-keys ...] [--add-public-key ...] [--remove <key>] [--current ...] [--current-revision n] [--railway --project --environment --service --yes]` | Renders `NodeAdmin__RootPublicKeys`/`NodeAdmin__RootKeyRevision`. `--current-revision 0` is the valid bootstrap state (nothing deployed yet) — the rendered revision is clamped to ≥ 1 whenever the key list being rendered is non-empty. See CONTRACT ASSUMPTIONS below. |
| `profile add <name> --origin <origin> --kind root\|admin --key-name <n> [--use]` / `profile list` / `profile use <name>` | Local-only, never touches the network. Named `--origin`, not `--node` — see CONTRACT ASSUMPTIONS below. |
| `login [--root] [--totp <code>]` | Challenge → canonical-sign → session. Prompts for a key passphrase and/or TOTP as needed. |
| `logout` | Revokes the server-side session, then deletes the local session file regardless of network outcome. |
| `whoami` | `GET /me`. |
| `sessions list [--admin <id>]` / `sessions revoke <handle>` | `--admin` filters by the full `principal` (`admin:<id>`/`root:<fp>`); a bare id is auto-prefixed with `admin:`. Sessions are identified by an opaque `handle` — the real sid never leaves the access token (lane A1 M-1). Revoking a `root:*` session's handle requires a root caller. |
| `admins list\|show\|create\|suspend\|reactivate\|revoke\|activate\|revoke-sessions` | `create` prints a one-time enrollment token; `activate <id> --fingerprint <fp>` completes the H3 state machine. |
| `admins roles <id> --set <roleIds>` | `PUT /admins/{id}/roles` returns the same reenrollment shape as `mfa reset`: changing a still-*pending* admin's roles resets them to `pending_enrollment` (keys/TOTP invalidated); prints a fresh enrollment token when present (caller was root), otherwise warns that only root can issue one. |
| `keys add [--admin-id <id>] --key-name <n> [--label <l>] [--no-passphrase] [--force]` / `keys revoke <adminId> <fingerprint>` | Step-up gated (auto re-login-and-retry, see below); the proof is always signed by the *new* key. The key file is written to disk BEFORE the server call that depends on it existing, and deleted again if that call fails. |
| `mfa reset <adminId>` | Revokes the admin's keys/sessions and returns them to `pending_enrollment`. Returns a new enrollment token (shown once) **only when the caller is root** — a peer owner gets confirmation the reset happened, no token. |
| `enroll --origin <origin> [--profile-name <n>] [--key-name <n>] [--label <l>] [--no-passphrase] [--force]` | Reads the enrollment token from stdin or a hidden prompt — **never** a CLI argument; the token read and every later prompt (passphrase, confirm, TOTP) share one FIFO line queue over real stdin, so a script piping all of them in order works correctly. Named `--origin`/`--profile-name` — see CONTRACT ASSUMPTIONS below. Aborts (exit 3) if `/enroll/start`'s returned `audience`/`fingerprint` disagree with `--origin`/the local key — the same MITM-shaped check as login's H1. The key file is written to disk BEFORE `/enroll/complete`, and deleted again if that call fails. |
| `roles list\|create` / `roles update <id> --name <n> --permissions <p>` / `roles delete <id>` | `update` is a full replace (`PUT /roles/{id}` takes the same `{name, permissions}` body as `create`) — `--name` is required, not optional, and can rename the role. |
| `root revoke-sessions [--yes]` | `POST /root/revoke-sessions`, **root-only** — fails locally (exit 4) if the active profile isn't `kind: root`, before any network call. Destructive (ends every root session, including the caller's); prompts for `yes` confirmation unless `--yes`. |
| `audit tail [--after <seq>] [--limit <n>]` | `--follow` is accepted but errors — cut per plan.md §8. |
| `audit verify [--full] [--from-seq <n>]` | Recomputes and pins the chain locally, then cross-checks the server's own `GET /audit/verify?fromSeq=&full=`; non-zero exit (6) on any integrity alert. Defaults `fromSeq` to the local pin (incremental); `--full` ignores the pin and re-verifies from genesis, which the server may answer with `RATE_LIMITED` (429, exit 5). |
| `api <METHOD> <path> [--data <json>\|@file]` | `path` must start with `/api/`; absolute/protocol-relative URLs are rejected before any network call. |

Global flags on every command: `--profile <name>`, `--json`. There is no global `--node`
override — see CONTRACT ASSUMPTIONS below for why, and `profile add --origin`/`enroll --origin`
for the only ways a node origin is ever set.

## Exit codes

`0` ok · `2` usage (incl. `NOT_FOUND`, `CONFLICT` — a wrong id or stale state is a user-input
problem, not a permission/network fault) · `3` auth (`AUTH_FAILED`/`STEP_UP_REQUIRED`/`ADMIN_LOCKED`
— shows the retry time from the server's `Retry-After`, formatted with hours once the lockout
backoff is exponential enough to need them) · `4` permission
(`PERMISSION_DENIED`/`DOMINANCE_REQUIRED`/`ESCALATION_REFUSED`/`LAST_OWNER`/`SELF_ACTION_REFUSED`)
· `5` network/server (incl. `NODE_ADMIN_UNAVAILABLE` — the node has no `NodeAdmin__Audience`
configured — and `RATE_LIMITED`, also with a formatted retry time) · `6` audit integrity alert.
The CLI always prefers the envelope's `code` over the HTTP status; status is only a fallback when
the body isn't a parseable AZOAResult. Full mapping in `src/errors.ts`.

## CONTRACT ASSUMPTIONS

Recorded here per the track brief; flag to the orchestrator if lane A disagrees.

All routes, body field names and response shapes below were cross-checked against the real
`Controllers/NodeAdmin*Controller.cs`, `Models/{Requests,Responses}/NodeAdminResponses.cs` and
`Services/NodeAdmin/AGENTS.md` §contract-deviations in lane A1 (branch `feat/node-admin-a1`,
through commit `a90f6ab6`) — not guessed. Every `*Recent` (step-up) mutating command auto-retries
on `STEP_UP_REQUIRED` via the single `mutateWithStepUpRetry` seam (`src/mutate.ts`); this closed
an earlier-noted gap where only `keys add`/`api` did. Three more bugs — a non-TTY stdin reader
that starved every prompt after the first in a multi-prompt command, un-enveloped 4xx responses
collapsing to exit 5, and `root env`'s bootstrap `--current-revision 0` being rejected/silently
losing the revision line — were found and fixed by an independent live drive against the real
backend (`conductor/tracks/node-admin-rbac-cli_20261018/join-report.md` on the join branch) and
folded in here, each with new coverage (`test/cliProcess.test.ts`, `test/httpClient.test.ts`,
`test/rootEnv.test.ts`).

1. **Key file format** is a small JSON envelope, not literal PKCS8 PEM (plan.md §5 says "PEM").
   The binding CLI security amendments (scrypt N=2^17 r=8 p=1 256 MiB + AES-256-GCM) don't
   correspond to any OpenSSL-standard encrypted-PEM envelope Node can emit natively, and the
   format never crosses the wire — it's local-only. See `src/AGENTS.md`.
2. **`root env` has no wire contract** with the node at all — it's a local env-line renderer.
   Without `--railway` it can't know the currently-deployed value, so `--current`/
   `--current-revision` let the operator supply it explicitly. With `--railway`, `--project`/
   `--environment`/`--service` are required (no reliance on a locally linked project); the CLI
   never shells out — it resolves a real `railway.exe` on PATH, or (if only the npm
   `@railway/cli` shim is installed) the bundled binary/`.js` npm installs beside the shim
   (`<dir>/node_modules/@railway/cli/bin/railway.exe`, falling back to running the bundled
   `railway.js` via `process.execPath`) — and calls `railway variable list --project P
   --environment E --service S --json` to read, `railway variable set KEY --stdin --project P
   --environment E --service S [--skip-deploys]` once per changed variable to write (the value
   goes over stdin, never argv; `--skip-deploys` on every call but the last, so a key removal +
   revision bump trigger exactly one redeploy). Every project/environment/service id, public
   key, and the removal target is validated against a closed character class *before* any
   process is spawned — see `src/commands/rootEnv.ts` and `test/rootEnv.test.ts`.
3. **`enroll`'s `--origin` is mandatory** and supplies the audience pin, since there is no local
   profile yet at enrollment time (the admin is enrolling fresh, from their own machine). It
   (and `profile add --origin`) is named `--origin` rather than `--node` for two independent
   reasons: Commander resolves a subcommand option with the same flag name as a parent option
   against the PARENT's definition (verified via a minimal repro during implementation — a
   same-named subcommand `requiredOption` looked unset even when a value was given), and — after
   the H1 fix below — there is no global `--node` at all to collide with anymore anyway.
   `--profile-name` on `enroll` exists for the analogous reason against the global `--profile`.
4. **H1 fix removed the global `--node` override entirely** (it previously let an authenticated
   command be redirected to a different origin while still attaching the real bearer token, and
   doubled as the audience the H1 check compared itself against — meaning an attacker who
   controlled the override controlled both sides of that check). `src/context.ts`'s
   `resolveProfile` now always reads `profile.node` straight from `~/.azoa/config.json`, with no
   override parameter at all.
5. **`keys add`'s target admin** defaults to the logged-in admin (self); the C1 "dominance"
   case (root or a node-owner-equivalent adding a key for *another*, e.g. locked-out, admin) is
   wired as an explicit `--admin-id` override — the new keypair is still always generated and
   self-signed on whichever machine runs the command, never derived from an existing key.
6. **`login --totp <code>`** is an additional non-interactive escape hatch for scripting; H3's
   "never argv" rule is scoped to the enrollment *token* specifically, not every TOTP code.
