---
type: track-spec
track: node-admin-rbac-cli_20261018
created: 2026-10-18
status: pending
related:
  - user-dapp-rbac
  - platform-stub-closeout_20261015
---

# Node admin identity, RBAC, MFA and the `azoa-node` CLI

## Why

Today a node has exactly one administrator: the reserved operator identity
(`a20a0000-0000-4000-8000-000000000001`) with a password seeded from env. Every audit
row names that same id, there is no MFA, no way to create another admin, and the only
recovery is "edit env, redeploy". The product direction is an IT platform that banks and
financial institutions run for themselves and for others, which needs per-human
accountability, separation of duties and a scriptable admin surface.

## Owner decisions (2026-10-18)

| # | Decision |
|---|---|
| A-1 | **Root key = Ed25519 keypair.** `azoa-node keygen` creates it on the holder's machine; only the public key goes into the cloud env (`NodeAdmin__RootPublicKeys`). Holding cloud access + the private key = break-glass root. The node never sees the private key. |
| A-2 | **Named admins + break-glass root.** Root bootstraps and recovers; each human admin has their own account, own Ed25519 key(s), own audit identity. |
| A-3 | **Full RBAC suite + authenticator MFA.** Permission catalog, built-in and custom roles, TOTP (RFC 6238, Google Authenticator-compatible) as second factor for named admins. Four-eyes approval is designed for, built later. |
| A-4 | **Node.js CLI in repo** at `tools/azoa-node-cli` (TypeScript), cloud-agnostic, optional Railway adapter. |

## Acceptance

1. A cloud-access holder can run `azoa-node keygen`, put the printed public key in the
   node env, and `azoa-node login --root` obtains a short session (Ed25519 challenge).
2. Root can create the first node admin; that admin enrolls from their own machine
   (own keypair + TOTP) via a one-time enrollment token, and the private key never
   leaves their machine.
3. Named admins log in with Ed25519 signature **and** a TOTP code; sessions are short,
   revocable per admin and globally, and step-up re-auth is required for sensitive writes.
4. RBAC: every existing operator route is mapped to a permission; built-in roles
   (node-owner, governance, compliance, operations, auditor) and custom roles; nobody
   can grant a permission they do not hold; the last node-owner cannot be removed or
   demoted; root can always mint a node-owner.
5. Append-only, hash-chained `node_admin_audit` records every login (success and
   failure), root use, admin/role/key/MFA change and every permission-gated write, naming
   the human actor.
6. The legacy reserved operator + web console keep working unchanged until a later
   migration track retires them.
7. CLI: `keygen`, `root env`, `login`, `logout`, `whoami`, `admins …`, `roles …`,
   `keys …`, `mfa …`, `enroll`, `audit tail`, `api <METHOD> <path>` passthrough. Session
   tokens stored 0600 under `~/.azoa/`, private keys optionally passphrase-encrypted.
8. Tests model real flows (root bootstrap → create admin → enroll → login with TOTP →
   permitted vs forbidden call → revoke) through the HTTP seam; CLI protocol tested
   against the real crypto (no mocked signatures).
