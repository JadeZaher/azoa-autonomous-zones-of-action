---
type: UX Study
title: AZOA node administration release UX study
description: Guarded local release study for node administration, operator recovery and developer dashboard journeys.
timestamp: "2026-10-18"
---

# AZOA release UX study

AZOA provides developer wallet and tenant services with a separate operator console. This release introduces node-admin RBAC and a CLI. The browser study checks how people understand permissions, sign in, recover from unavailable authority and inspect operational state. Dedicated CLI/integration probes establish capability and authorization separately from usability evidence.

The target is the disposable `azoa-e2e` stack at `http://localhost:3200`, backed by its own API on 5200 and unexposed SurrealDB volume. The plugin guard accepts the loopback target; the adapter additionally proves named container port mappings, Development environment and fake Identomat/Stripe origins inside the API container. `/api/health` is readiness, not a unique deployment identity. This study never targets Railway. The coordinator owns stack startup/reset outside study lanes. The initial pass has executed; its recorded failures and reviewed fixes await a refreshed-image pass.

Use only marked `qa-uxqa-*` personas. Ordinary accounts are registered through the API and signed in through the UI. The operator persona uses only the dedicated E2E stack's pre-seeded operator credential. It may inspect read-only screens. The adapter does not mint node-admin credentials. The release owner's live root-key drill remains a distinct owner-operated release gate.

One narrow coordinator fixture exception is prepared for a separately authorized tenant-designer scenario: the coordinator may assign only `dapp:developer` and issue one one-day fixed-scope tenant credential to the single ordinary `qa-uxqa-ai-tenant` account. It uses the disposable E2E operator's real HTTP policies, never an owner root key or database writes. See `fixtures/tenant-designer.md`. This does not authorize UX/probe lanes to grant roles, mint keys or execute the fixture. No fixture privilege changes have been performed during preparation.

Routes are Next app-router pages in `frontend/src/app`; `routes.json` inventories 57 pages and 3 frontend BFF route patterns. It does not inventory all .NET API routes. REST API coverage reports only observed requests; it must not be described as complete backend coverage. Operator routes include legacy operational screens with authorization semantics that require review against the new node-admin track.

Gold tasks measure finding sign-in from an expired/anonymous operator visit, reaching one's own settings, and locating the operational audit view. Predicate success alone does not establish that any privileged feature or audit event is correct. Six lanes include mobile/low-literacy, keyboard-only, zoom/slow-network, operator, gold tasks, and a separate capability probe. Four research lenses focus on accessibility, service handoffs, copy and trust; findings remain hypotheses until code/evidence confirmation.

Never initiate value transfers, real chain transactions, live KYC, outbound notifications, real-user changes, root-key operations, deployment changes or environment edits. No email sink was proved during preparation, so all notification-triggering actions are forbidden. Ordinary registration was inspected: `Controllers/UserController.cs:38` delegates to `Managers/UserManager.cs:32`, which persists through `Providers/Stores/Surreal/SurrealUserStore.cs:63` without notification delivery. Recheck that call chain if the integrated release changes it; a newly introduced notification blocks member seeding until sink behavior is proved. Traces contain auth and remain private. No externally published artifact includes session state or traces.

The frontend verification commands below are a coordinator checklist. Run them once at the end of the complete fix batch, with the SDK built first. Existing E2E journeys provide functional evidence alongside this study; their results cannot be substituted for the study's missing journey traces. Two consecutive passing passes are required by the plugin before claiming the study exit criteria are met.

```json uxqa-config
{
  "product": "AZOA node administration release",
  "baseUrl": "http://localhost:3200",
  "guard": { "path": null, "forbidHosts": ["railway.app", "up.railway.app", "azoa.io", "azoa.app"] },
  "session": { "cookieName": "azoa_operator_session", "cookieDomain": null, "sessionsFile": "uxqa/.secrets/sessions.json" },
  "adapter": {
    "seed": "node uxqa/adapter/adapter.mjs seed {personas} {out}",
    "guard": "node uxqa/adapter/adapter.mjs guard",
    "link": "node uxqa/adapter/adapter.mjs link"
  },
  "routes": { "dir": "frontend/src/app", "framework": "next-app", "ignorePrefix": null },
  "api": { "style": "rest", "prefixes": ["/api/"], "trpcPrefix": "/api/", "proceduresFile": null },
  "playwright": "./frontend/node_modules/playwright/index.mjs",
  "browserChannel": null,
  "axePath": null,
  "marker": "uxqa",
  "attemptBudget": 3,
  "lenses": ["service-designer", "accessibility", "content-designer", "trust-behavioural"],
  "goldTasks": [
    { "id": "gold-operator-recovery", "persona": "qa-uxqa-mobile", "start": "/operator/audit", "goal": "Understand how to sign in after my operator session has ended.", "success": { "urlMatches": "^/operator/login$", "roleVisible": { "role": "button", "name": "Enter operator console" } }, "maxSeconds": 90, "constraints": { "width": 390 } },
    { "id": "gold-member-settings", "persona": "qa-uxqa-gold-member", "start": "/overview", "goal": "Find the settings for my account and integration.", "success": { "urlMatches": "^/settings$", "roleVisible": { "role": "heading", "name": "Settings" } }, "maxSeconds": 150 },
    { "id": "gold-operator-audit", "persona": "qa-uxqa-operator", "start": "/operator", "goal": "Find the history of changes to identity verification policy.", "success": { "urlMatches": "^/operator/audit$", "textVisible": "KYC audit trail" }, "maxSeconds": 150 }
  ],
  "targets": { "goldSuccess": 0.8, "newHigh": 0, "frictionTolerance": 0.1, "routeCoverage": 0.2 },
  "fixRepo": null,
  "testCommand": "npm --prefix frontend test",
  "typecheckCommand": "npm --prefix frontend run typecheck",
  "buildCommand": "npm --prefix frontend run build",
  "baseRedFile": "INTEGRATION-TEST-PASSOFF.md",
  "hooks": { "afterFix": null, "waitDeploy": null, "beforePass": null },
  "extraRules": [
    "Only the disposable azoa-e2e stack on proved loopback bindings is allowed.",
    "Do not use owner root keys, production credentials or live Railway endpoints.",
    "Registration is allowed only while its inspected direct-persistence call chain remains free of outbound notifications.",
    "No monetary, provider, role, deployment or real-user mutations by UX/probe lanes. Only the coordinator may prepare the single marked tenant-designer role/key fixture described in fixtures/tenant-designer.md after its separate guard/scenario authorization.",
    "UI-only lanes may not inspect source or call the API; probes do not count as UI success.",
    "Finish every journey and record blocked lanes explicitly.",
    "Use native Codex collaboration; do not invoke unavailable Claude model aliases."
  ]
}
```
