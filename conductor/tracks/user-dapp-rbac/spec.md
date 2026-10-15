---
type: spec
track: user-dapp-rbac
created: 2026-07-10
status: shipped
horizon: alpha
depends_on:
  - user-sovereign-identity
  - tenant-consent-delegation
  - dapp-composition
related:
  - federation-v2
  - action-invitations-approval
---

# Track: user-dapp-rbac

## Goal

Introduce explicit user-level RBAC for DApp economies: some users only use
DApps, some users can build/manage DApps, and platform operators remain a separate
JWT-only authority. The current `DappDevelop` scope is the right enforcement hook,
but it needs a role-manager model around it so "logged-in user" does not equal
"DApp author."

## Role model

| Role | Meaning | Expected authority |
|---|---|---|
| DApp user | Default user. Can log in, view allowed DApps/actions, run public or invited flows, and manage its own identity/consent. | No DApp authoring, no DApp-series mutation, no template publication. |
| DApp developer | User trusted to author economic building blocks. | `dapp:develop`: create/edit holons, actions, templates, and draft DApp series owned by that user. |
| DApp manager | User trusted to manage a DApp/economy boundary. | `dapp:manage`: publish/compose/deploy DApp series, manage DApp membership/roles, grant developer access within owned scopes. |
| Operator | Platform/node admin. | `operator:admin`; JWT-only and never API-key issuable. Separate from DApp manager. |

The vocabulary must remain scope-shaped because existing API keys, child credentials,
and consent gates already understand `scope` claims. The role manager is responsible
for translating user role assignments into scoped JWT claims and, later, scoped
DApp membership checks.

## Enforcement boundaries

1. **JWTs are no longer implicitly DApp developers.** A self-owned user without a
   DApp role can use DApps but cannot reach DApp write surfaces.
2. **Scoped API keys remain least-privilege.** Empty legacy API-key scopes keep their
   existing full-access compatibility rule for now; non-empty keys must carry the
   required DApp scope.
3. **DApp managers are not operators.** `dapp:manage` can manage DApp membership and
   lifecycle inside owned DApp boundaries, but never key rotation, backfill, platform
   config, or cross-user admin endpoints.
4. **Ownership still matters.** A role allows reaching the write path; managers still
   enforce user/DApp ownership and membership.

## Acceptance criteria

1. User records can carry DApp role/capability assignments without breaking
   existing self-sovereign identity or tenant-managed child-user flows.
2. JWT issuance stamps only the user's allowed DApp scopes; ordinary users receive
   no `dapp:develop` or `dapp:manage` claim by default.
3. `DappDevelop` policy denies ordinary JWT users and API keys whose owning user
   no longer has a developer/manager role, even when the key carries stale scopes.
4. DApp-series authoring endpoints are gated by `dapp:develop`; composition lifecycle
   endpoints (`compose`, `generate`, `deploy`) are gated by `dapp:manage`; read/use
   endpoints remain available to authenticated DApp users subject to existing
   ownership/public/invite checks.
5. SDK/API discovery makes the distinction obvious: DApp users can use/run; DApp
   developers/managers can author/manage.
6. Tests cover at least: ordinary user denied DApp write, developer user allowed
   authoring but denied lifecycle management, manager user allowed lifecycle
   management, role-bound API-key scope discovery/issuance, and operator authority
   not granted by DApp manager role.

## Follow-ons

- Per-DApp membership table: manager/developer/user grants scoped to a single
  `DappSeries` or economy.
- UI role-management panel for DApp managers.
- Federation projection: DApp role grants become signed local facts when an economy
  opts into federation, never global admin claims.
