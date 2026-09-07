---
type: plan
track: user-dapp-rbac
created: 2026-07-10
status: shipped
---

# Plan: user-dapp-rbac

> **Written retroactively 2026-09-07.** The track shipped without a plan.md, which
> is why its `metadata.json` still read `in_progress` / "implementation swarm
> started". Every box below is ticked against code read at commit `b2576f4`, with
> the citation beside it — not against a memory of the swarm.

## Phase 1 — role model

1. [x] Add the DApp role vocabulary as first-class constants, kept scope-shaped so
   the existing API-key, child-credential, and consent-gate claim machinery keeps
   working unchanged. — `Core/AzoaDappRoles.cs` (`dapp:user` / `dapp:developer` /
   `dapp:manager`), `Core/AzoaScopes.cs:56` (`DappDevelop`) and `:62` (`DappManage`).
2. [x] Let a user record carry a role assignment without disturbing self-sovereign
   identity or tenant-managed child users. — `Models/Requests/UserRoleAssignmentModel.cs`;
   assignment route `PUT /user/{id}/dapp-role` at `Controllers/UserController.cs:124`.

## Phase 2 — enforcement

3. [x] Stop treating "logged-in JWT" as "DApp author": the `DappDevelop` and
   `DappManage` policies require the scope claim **and** the owning user's live
   role. — `Program.cs:604-651`. The `&& ctx.User.HasDappDeveloperRole()` conjunct
   at `:625` (and `:643` for manage) is the load-bearing half: it is what makes a
   stale scope on an old API key worthless once the role is revoked.
4. [x] Gate authoring writes on `dapp:develop`. — `Controllers/DappSeriesController.cs`
   lines 42, 54, 66, 90, 102, 113, 125.
5. [x] Gate lifecycle writes (compose / generate / deploy) on `dapp:manage`, keeping
   them strictly separate from `operator:admin`. — `Controllers/DappCompositionController.cs`
   lines 27, 68, 79.
6. [x] Keep read/use endpoints open to authenticated DApp users, still subject to the
   pre-existing ownership / `IsPublic` / invite checks. — no change was required;
   the invite half is `action-invitations-approval`.

## Phase 3 — discovery and evidence

7. [x] Make the distinction discoverable through scope issuance rather than prose:
   `operator:admin` is unissuable to API keys, and only the self-issuable set is
   offered. — `Core/AzoaScopes.cs:153` (`ApiKeyForbiddenScopes`) and `:198`
   (`SelfIssuableScopes`).
8. [x] Cover the five behaviours the spec's AC6 named. —
   `tests/AZOA.WebAPI.Tests/Controllers/DappCompositionAuthorizationTests.cs`,
   `tests/AZOA.WebAPI.IntegrationTests/Controllers/ApiKeyScopeRbacIntegrationTests.cs`,
   `tests/AZOA.WebAPI.IntegrationTests/Controllers/DappSeriesControllerIntegrationTests.cs`.

## Explicitly not in this track

The spec's three "Follow-ons" were never in scope and are not defects:

- Per-DApp membership table (manager/developer/user grants scoped to a single
  `DappSeries`) — a role today is node-wide, not per-economy.
- A role-management UI panel for DApp managers. Note that `/admin/roles` exists as an
  **operator** surface; a manager-facing per-DApp panel does not.
- Federation projection of role grants as signed local facts — inherits
  `federation-v2`'s activation gate.
