---
type: evidence
track: node-admin-rbac-cli_20261018
phase: P5
created: 2026-10-18
---

# Join report — node-admin-rbac-cli_20261018 (P5)

Integration worktree: `../oasis-sleek-node-admin-join`, branch `join/node-admin` off
`feat/platform-stub-closeout`. Merges of `feat/node-admin-a1` (lane A, backend) and
`feat/node-admin-cli` (lane B, `azoa-node` CLI) were both clean fast-forward-free merges —
the two lanes own disjoint paths (`Services/NodeAdmin/**`, `Controllers/NodeAdmin*`,
`Persistence/SurrealDb/**NodeAdmin*`, `tests/**/NodeAdmin*` vs `tools/azoa-node-cli/**`) exactly
as the plan's lane split intended, so there were **no conflicts to resolve, trivial or
otherwise**.

## Commits on `join/node-admin`

```
1a117d9a fix(node-admin-join): root env bootstrap rejected/mis-rendered revision 0 -> 1
2df57848 fix(node-admin-join): CLI exit code for un-enveloped 4xx collapsed to NETWORK(5)
ca4d5f97 fix(node-admin-join): CLI piped-stdin reader starved multi-prompt flows
ff4a2d6a merge(join): node-admin-cli — azoa-node CLI against plan contract
6645470e merge(join): node-admin-a1 — identity, ed25519+totp auth, sessions, RBAC, audit chain
```
(remaining history is the two lanes' own commits, unchanged.)

All three fixes are **CLI-side only**. The backend (lane A1) held up through the entire
real end-to-end drive with no changes needed.

## Build

`dotnet build azoa.sln -c Debug` — **0 errors**, 17 pre-existing warnings (nullable/xUnit
style, unrelated to this track; no new warnings introduced by the merge).

## Environment

- SurrealDB 3.1.4 podman container `azoa-join-surrealdb`, memory storage, `--user 0:0`, bound
  to host port 8042.
- Schema: namespace `joinns` / database `joindb`, all 145 `Persistence/SurrealDb/Generated/Schemas/*.surql`
  files applied as one batch via `/sql` (same mechanism `tests/.../SurrealTestSchema.cs` uses —
  `surrealforge`'s CLI binary isn't installed locally, so the equivalent idempotent
  `DEFINE ... IF NOT EXISTS` DDL was POSTed directly). All 10 `node_admin*` tables confirmed
  present via `INFO FOR DB`.
- API: `dotnet run --project AZOA.WebAPI.csproj -c Debug --no-build --no-launch-profile`,
  `ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_URLS=http://localhost:5077`.
  `--no-launch-profile` was required — `Properties/launchSettings.json` otherwise overrides
  `ASPNETCORE_URLS` back to port 5000 even with the env var set.
- `NodeAdmin__RootPublicKeys`/`NodeAdmin__RootKeyRevision=1`/`NodeAdmin__Audience=http://localhost:5077`
  — plain `http` is explicitly accepted in `NodeAdminRuntime.ResolveAudience` (`uri.Scheme is
  "https" or "http"`), so no Development-only relaxation was needed.
  `SurrealRuntime__Endpoint/Namespace/Database/User/Password` pointed at the throwaway container
  (config section is `SurrealRuntime`, not `SurrealDb` — `SurrealRuntimeConfigurationGuard`
  resolves it that way whenever the section exists, in any environment).
  Two throwaway Fiat values (`Fiat__Test__SecretKey`, `Fiat__Test__WebhookSecret`) were also
  required at boot — pre-existing `FiatRailStartup.Validate` guards, unrelated to this track.
- CLI: `tools/azoa-node-cli` — `npm ci && npm run build`. Per-identity `AZOA_HOME` under the
  scratchpad (`join-azoa-home/{root,alice,bob}`), honoring the CLI's own `AZOA_HOME` override
  (`src/storage/paths.ts`).
- TOTP codes computed with a small RFC 6238 (SHA1/6/30) helper
  (`.../scratchpad/totp.mjs`), cross-checked against the exact algorithm the CLI's own
  `test/fixtures/totp.ts` uses (byte-for-byte identical implementation) — not guessed.

## Command table

| # | Actor | Command | Exit | Outcome |
|---|---|---|---|---|
| 1 | root | `keygen --name joinroot --kind root` (passphrase+confirm via stdin) | 0 | key generated; fp `6be54867d7c8471c` |
| 2 | root | `profile add rootp --origin http://localhost:5077 --kind root --key-name joinroot --use` | 0 | profile created |
| 3 | root | `login --root --profile rootp` (passphrase via stdin) | 0 | `root:6be54867d7c8471c`, 5 perms |
| 4 | root | `whoami --profile rootp` | 0 | session + root fingerprint shown |
| 5 | root | `admins create --username alice --display-name Alice --role node-owner --profile rootp` | 0 | admin `2601bd14-…`, enrollment token captured |
| 6 | alice | `enroll --origin … --profile-name alice --key-name alice-key --no-passphrase` (token+TOTP via scripted stdin) | 0 | `pending_activation`, fp `dad96eea36c6e0a6` |
| 7 | root | `admins activate 2601bd14-… --fingerprint dad96eea36c6e0a6 --profile rootp` | 0 | activated |
| 8 | alice | `login --profile alice --totp <code>` | 0 | `admin:2601bd14-…`, 18 perms (node-owner) |
| 9 | alice | `whoami --profile alice` | 0 | full admin record, `mfaEnrolled: true` |
| 10 | alice | `roles list --profile alice` | 0 | 5 built-in roles (auditor/compliance/governance/node-owner/operations) |
| 11 | alice | `admins create --username bob --display-name Bob --role auditor --profile alice` | 0 | admin `eb423c0a-…`, enrollment token captured |
| 12 | bob | `enroll --origin … --profile-name bob --key-name bob-key --no-passphrase` (token+TOTP via scripted stdin) | 0 | `pending_activation`, fp `16f50bb7f501b3ce` |
| 13 | alice | `admins activate eb423c0a-… --fingerprint 16f50bb7f501b3ce --profile alice` | 0 | activated |
| 14 | bob | `login --profile bob --totp <code>` | 0 | `admin:eb423c0a-…`, 7 perms (auditor) |
| 15 | bob | `admins suspend 2601bd14-… --profile bob` | **4** | `PERMISSION_DENIED` — auditor lacks `admins.manage` outright (never reaches the dominance check) |
| 16 | alice | `api GET /api/node-admin/me --profile alice` | 0 | `/me` passthrough returns alice's own record |
| 17 | alice | `api GET https://evil/x --profile alice` | **2** | rejected pre-network: `path must start with '/api/'` |
| 18 | alice | `sessions list --profile alice` | 0 | one row, **`handle` only** — no raw `sid` (M-1) |
| 19 | alice | `sessions revoke 739cb650… --profile alice` (own handle) | 0 | revoked |
| 20 | alice | `whoami --profile alice` (session now revoked) | **3** | `AUTH_FAILED` (see fix #2 below — was exit 5 before the fix) |
| 21 | alice | `login --profile alice --totp <code>` (fresh session) | 0 | re-authenticated |
| 22 | alice | `audit tail --limit 10 --profile alice` | 0 | hash-chained rows from seq 1, genesis `prevHash` = 64 zeros |
| 23 | alice | `audit verify --profile alice` (incremental) | 0 | `{ok:true, pin:{seq:24, hash:"2372…488"}}` |
| 24 | alice | `audit verify --full --profile alice` | 0 | same pin, full-chain reverify ok |
| — | — | pin file `~/.azoa/audit-pins/alice.json` | — | created, matches the printed pin exactly |
| 25 | alice | `root revoke-sessions --yes --profile alice` | **4** | local refusal before any network call: "Only a root session can end root sessions." |
| 26 | root | `root revoke-sessions --yes --profile rootp` | 0 | `{ok:true}` — every root session ended |
| 27 | root | `whoami --profile rootp` (session now revoked) | **3** | `AUTH_FAILED` |
| 28 | root | `root env --profile-keys rootp --current "" --current-revision 0` | **0** | renders both `NodeAdmin__RootPublicKeys=…` and `NodeAdmin__RootKeyRevision=1` (see fix #3 below — was exit 2 before the fix) |

Rows 1–14 and 16–19, 21–24, 26, 28 are the "happy path"; rows 15, 17, 20 (pre-fix), 25, 27 are
the negative/refusal checks the task asked for.

## Fixes (all CLI-side; backend required none)

### 1. `src/io/input.ts` — piped-stdin reader starved multi-prompt commands

`readSecret`/`readPipedStdin`, when stdin wasn't a TTY, slurped the **entire** stream to EOF on
their first call. `keygen` (passphrase + confirm) and `enroll` (token, passphrase, confirm,
TOTP) each need 2–4 sequential reads in one invocation; only the first ever got an answer, the
rest got `""`. Never caught by the CLI's own test suite because it drives prompts through a
`FakeInputSource`, never real stdin.

A `readline.Interface` + `question()` per call was tried next and *also* failed: when multiple
newline-terminated answers arrive in one piped write (the normal case for a `printf`/heredoc),
Node delivers them as a single `'data'` chunk; `question()` only resolves for the first line in
that chunk, and the rest are emitted as `'line'` events with no listener and silently dropped —
the next read then hangs forever.

**Fix**: a hand-rolled buffered line reader over `process.stdin` that queues full lines (or
pending resolvers) in strict FIFO order, immune to chunking. Verified against the real built CLI
(root `keygen` with piped passphrase+confirmation, then the full `enroll` token→TOTP sequence
for both alice and bob) and the CLI's own vitest suite (60/60 green).

### 2. `src/http/client.ts` — non-enveloped 4xx collapsed to exit 5 (NETWORK)

A revoked/expired/malformed bearer token is rejected by the ASP.NET Core JWT bearer middleware
itself (`OnTokenValidated` → `context.Fail`), which emits the framework's default 401 challenge
with an **empty body** — correct, expected backend behavior; this happens in the authentication
pipeline before any controller or `AZOAResult` envelope exists, for every endpoint in the app.
`client.ts`, finding no parseable envelope, fell through to a blanket `AzoaCliError.network(...)`
for *any* status ≥ 400 — so `whoami` after `sessions revoke <own>` exited 5 instead of 3, even
though the CLI's own documented contract (README "Exit codes": 3 = AUTH, 4 = PERMISSION) and
`Services/NodeAdmin/AGENTS.md`'s HTTP-status table both say 401 = `AUTH_FAILED`, 403 = the
permission family, independent of whether a body is present.

**Fix**: map the bare status to the same error-code family the envelope path would use
(401/403/404/409/423/429) before falling back to NETWORK for anything unmapped. Verified live
(row 20 above went from exit 5 → exit 3) and against the vitest suite (60/60 green).

### 3. `src/commands/rootEnv.ts` — bootstrap rejected, then mis-rendered, revision 0→1

The README's own documented first-time bootstrap command,
`root env --profile-keys <p> --current "" --current-revision 0`, failed outright
(`"Invalid revision: must be a positive integer, got '0'"`) — but 0 is exactly the correct value
of `--current-revision` before any root key has ever been deployed (the option names the
*already-deployed* revision, not the one about to be rendered). Past that, the output logic was
**also** wrong: `nextRevision` started equal to `currentRevision` and was only ever bumped on
`--remove`, never on addition — so bootstrapping zero keys → one key would have rendered
`NodeAdmin__RootPublicKeys=…` with **no** `NodeAdmin__RootKeyRevision` line at all, which
`NodeAdminRuntime.cs` treats as "root keys configured with revision 0" and disables root login
on boot.

**Fix**: accept 0 as a valid bootstrap `--current-revision`, and clamp the rendered revision to
≥ 1 whenever the key list being rendered is non-empty, mirroring the server's own invariant.
Verified live — the fixed command is literally what rendered the `NodeAdmin__RootPublicKeys`/
`NodeAdmin__RootKeyRevision=1` pair this join test booted the API with — and against the vitest
suite (60/60 green).

## What was NOT found broken

- Ed25519 challenge/sign/verify round trip (root and admin logins) — correct on the first try,
  both directions.
- Audience pinning (H1), canonical signing (H2) — never exercised a mismatch path in this drive
  (both sides always agreed on `http://localhost:5077`), but the happy path round-tripped
  cleanly through real signatures end to end.
- RBAC dominance/escalation: auditor (bob) correctly refused `admins suspend` on alice outright
  (`PERMISSION_DENIED`, since auditor never holds `admins.manage` — doesn't even reach the
  dominance check) — exactly the plan's C1 invariant.
- Server-side sessions (C2): revoking alice's own session immediately invalidated her bearer
  token on the next request; `sessions list` never exposed a raw `sid`, only the opaque
  `handle` (M-1).
- Audit hash chain: 24 real rows (logins, enroll start/complete, activations, admin creation,
  session revokes) verified clean both incrementally and from genesis; the local pin file
  matched the printed pin exactly.
- Root-only session revocation (NEW-2): alice's attempt to run `root revoke-sessions` was
  refused **locally**, before any network call, consistent with the CLI's own documented
  behavior.

## Reap

- API process (`dotnet run`'s child `dotnet AZOA.WebAPI.dll`) stopped.
- SurrealDB container `azoa-join-surrealdb` stopped and removed (`podman rm -f`).
- Temp `AZOA_HOME` directories under the scratchpad (`join-azoa-home/{root,alice,bob}`) deleted
  — throwaway Ed25519 keys only, nothing else.
- No other worktree, container, or process was touched.
