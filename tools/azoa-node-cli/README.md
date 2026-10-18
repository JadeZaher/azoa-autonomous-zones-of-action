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
   azoa-node admins create --username alice --display-name "Alice" --role role-node-owner --profile rootp
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
  a passphrase; admin keys may opt out with `--no-passphrase`.
- **File permissions**: every file under `~/.azoa/` is written then locked to the current user
  only — `chmod 0600` on POSIX, `icacls ... /inheritance:r /grant:r "<user>:F"` (verified by
  re-reading the ACL) on Windows. A warning is printed if `~/.azoa/keys` resolves under a
  OneDrive-synced path.
- **Audience pinning (H1)**: the CLI refuses to sign anything if the server's challenge
  `audience` does not exactly match the profile's configured `--node` origin. This is the one
  check standing between a real login and a MITM/relay replaying a challenge from a different
  node.
- **Canonical signing (H2)**: every signed message is the server's own returned strings,
  joined verbatim — the CLI never re-serializes a timestamp or id itself. See
  `src/crypto/canonical.ts`.
- **Step-up**: `keys add` and the generic `api` passthrough both require (or transparently
  re-establish) a session whose `auth_time` is within the server's recent-login window;
  `api` retries the original request exactly once after re-login.
- **Audit pins**: `audit verify` recomputes the hash chain locally from `~/.azoa/audit-pins/<profile>.json`
  and alerts (exit code 6) on any tampered row or a head that regressed behind what was
  previously pinned — see `src/audit/chain.ts`.
- `azoa-node` never reads or prints `.env` values, and never forwards its bearer token to any
  origin other than the profile's configured node.

## Command reference

| Command | Notes |
|---|---|
| `keygen --name <n> [--kind root\|admin] [--no-passphrase]` | Root keys always require a passphrase. |
| `root env [--profile-keys ...] [--add-public-key ...] [--remove <key>] [--current ...] [--current-revision n] [--railway --project --environment --service --yes]` | Renders `NodeAdmin__RootPublicKeys`/`NodeAdmin__RootKeyRevision`. See CONTRACT ASSUMPTIONS below. |
| `profile add <name> --origin <origin> --kind root\|admin --key-name <n> [--use]` / `profile list` / `profile use <name>` | Local-only, never touches the network. `--origin` (not `--node`) to avoid colliding with the global `--node` override flag. |
| `login [--root] [--totp <code>]` | Challenge → canonical-sign → session. Prompts for a key passphrase and/or TOTP as needed. |
| `logout` | Revokes the server-side session, then deletes the local session file regardless of network outcome. |
| `whoami` | `GET /me`. |
| `sessions list [--admin <id>]` / `sessions revoke <sid>` | |
| `admins list\|show\|create\|suspend\|reactivate\|revoke\|activate\|roles\|revoke-sessions` | `create` prints a one-time enrollment token; `activate <id> --fingerprint <fp>` completes the H3 state machine. |
| `keys add [--admin-id <id>] --key-name <n> [--label <l>] [--no-passphrase]` / `keys revoke <adminId> <fingerprint>` | `add` requires a fresh (≤10 min) session and re-logs-in transparently if stale; the proof is always signed by the *new* key. |
| `mfa reset <adminId>` | Returns a new enrollment token (shown once). |
| `enroll --origin <origin> [--profile-name <n>] [--key-name <n>] [--label <l>] [--no-passphrase] [--force]` | Reads the enrollment token from stdin or a hidden prompt — **never** a CLI argument. `--origin`/`--profile-name` (not `--node`/`--profile`) to avoid colliding with the global flags of the same purpose-adjacent name. |
| `roles list\|create\|update\|delete` | |
| `audit tail [--after <seq>] [--limit <n>]` | `--follow` is accepted but errors — cut per plan.md §8. |
| `audit verify` | Recomputes and pins the chain; non-zero exit (6) on any integrity alert. |
| `api <METHOD> <path> [--data <json>\|@file]` | `path` must start with `/api/`; absolute/protocol-relative URLs are rejected before any network call. |

Global flags on every command: `--profile <name>`, `--json`, `--node <origin>` (override the
profile's configured node for one invocation).

## Exit codes

`0` ok · `2` usage · `3` auth (`AUTH_FAILED`/`STEP_UP_REQUIRED`/...) · `4` permission
(`PERMISSION_DENIED`/`DOMINANCE_REQUIRED`/`ESCALATION_REFUSED`/`LAST_OWNER`/`SELF_ACTION_REFUSED`)
· `5` network/server · `6` audit integrity alert. Full mapping in `src/errors.ts`.

## CONTRACT ASSUMPTIONS

Recorded here per the track brief; flag to the orchestrator if lane A disagrees.

1. **Key file format** is a small JSON envelope, not literal PKCS8 PEM (plan.md §5 says "PEM").
   The binding CLI security amendments (scrypt N=2^17 r=8 p=1 256 MiB + AES-256-GCM) don't
   correspond to any OpenSSL-standard encrypted-PEM envelope Node can emit natively, and the
   format never crosses the wire — it's local-only. See `src/AGENTS.md`.
2. **`root env` has no wire contract** with the node at all — it's a local env-line renderer.
   Without `--railway` it can't know the currently-deployed value, so `--current`/
   `--current-revision` let the operator supply it explicitly. The Railway CLI flag syntax used
   in `createRealRailwayAdapter()` (`railway variables --json`, `railway variables --set K=V`)
   is best-effort, not verified against a live `railway` binary in this environment.
3. **`enroll`'s `--origin` is mandatory** and supplies the audience pin, since there is no local
   profile yet at enrollment time (the admin is enrolling fresh, from their own machine). It
   (and `profile add --origin`) is named `--origin` rather than `--node` specifically because
   Commander resolves a subcommand option with the same flag name as a parent/global option
   against the parent's definition — passing `--node` to `profile add` or `enroll` left the
   subcommand's own `requiredOption` looking unset even though a value was given. Verified via
   a minimal Commander repro during implementation; `--profile-name` on `enroll` exists for the
   same reason, distinct from the global `--profile`.
4. **`keys add`'s target admin** defaults to the logged-in admin (self); the C1 "dominance"
   case (root or a node-owner-equivalent adding a key for *another*, e.g. locked-out, admin) is
   wired as an explicit `--admin-id` override — the new keypair is still always generated and
   self-signed on whichever machine runs the command, never derived from an existing key.
5. **`login --totp <code>`** is an additional non-interactive escape hatch for scripting; H3's
   "never argv" rule is scoped to the enrollment *token* specifically, not every TOTP code.
