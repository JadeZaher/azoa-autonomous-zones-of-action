---
type: UX Lane Report
title: Operator readiness UI observations
---

Outcome: COMPLETED for read-only readiness inspection. The existing operator session authenticated. Permission denial and expired-session recovery were not induced; those remain untested in this lane. No application source, tests, APIs, session file or other lane reports were read. No administrative control was activated.

Persona: qa-uxqa-operator, patience 3, desktop 1366 by 900. Entry: study-authorized /operator. All subsequent pages used visible navigation links. The operator could identify Development node, persistence ready, 0 enabled/ready KYC profiles, 0 tenant selections, and two deployment checklist actions needed. Session security copy distinguishes ending this browser session from global revocation after exposure or handoff. Neither action was clicked.

Evidence directory (private): runs/qa-uxqa-operator--operator-readiness/. trace.zip and ux.json are the finished run. Screenshots: node-overview.png, audit.png, audit-settled.png, tenants.png, providers.png. Replay: flows/operator-readiness/replay.mjs. The replay was syntax checked, not re-executed.

Observed facts:

- Audit link reached /operator/audit with KYC audit trail. After the visible loading message settled, it said no KYC control changes have been recorded and explained future changes. No events existed to inspect.
- Tenants link reached Tenant KYC configuration: 0 loaded, configured and selectable providers, with a specific no-active-tenants message.
- Providers link reached four disabled profiles, configuration flags without secret values, and a Development-only warning for manual review. Every profile displayed Updated Dec 31, 1969, 5:00 PM. providers.png records that display. The cause is unconfirmed.
- The wide operator navigation hides later entries outside the initial horizontal view in providers.png. Playwright brought the Audit link into view before clicking; there was no failed attempt. Human discoverability of the horizontal navigation requires validation.
- Axe WCAG scans recorded color-contrast (serious): Node 8 nodes, Audit 2, Tenants 1, Providers 1. Counts are detected nodes at scan time, not unique product defects. This lane did not inspect code or contrast selectors.

Expected/actual/recovery: readiness, audit, tenants and provider summaries should be discoverable without changing state. All four were reached through visible UI with no failed attempt, workaround or abandonment. Readiness problems were explained as configuration needs; actual provider operation and privilege enforcement were not tested.

Final harness metrics: 82 seconds; 3 clicks; 0 dead/rage clicks; 0 inputs; 0 validation errors; 5 navigation events, 4 distinct pages, 1 revisit; 0 HTTP errors, 0 console errors, 0 failed attempts, 0 workarounds; axeViolations 4 (rule-route occurrences), friction 4; 7 naturally observed API operations. No direct API requests were made by the lane.

Commands executed for this lane:

1. Required adapter guard, default sandbox: REFUSED IN_TARGET_GUARD (exit 1).
2. Identical guard with approved host access: OK azoa-e2e Development fake-vendors loopback-bindings (exit 0).
3. node uxqa/passes/01-release-readiness/flows/operator-readiness/interactive.mjs, first without TTY: immediate finish BLOCKED, 0 pageviews, 0 clicks, approximately 1 second. Its same-task artifact was overwritten by the explicitly disclosed TTY restart. No failure is concealed as success.
4. Same node command with TTY: 5 saved UI command blocks, then FINISH; completed metrics above. Every created session called finish in finally.
5. node --check uxqa/passes/01-release-readiness/flows/operator-readiness/replay.mjs: exit 0. Syntax verification only.

Guard invocation: UXQA_PLUGIN_LIB=C:/Users/atooz/.codex/plugins/cache/local/ux-qa-research/0.1.0/lib; UXQA_CONTAINER_RUNTIME=podman; UXQA_E2E_ROOT=C:/Users/atooz/Programming/Projects/oasis-sleek/.worktrees/node-admin-release/frontend/e2e; node uxqa/adapter/adapter.mjs guard.

Research hypotheses, not confirmed defects: epoch dates may reduce trust in freshness; off-screen navigation may slow interrupted operators; low contrast may impede status reading. Validate with five interrupted operators, asking each to explain freshness and find Audit without coaching; include low-vision participants. Permission recovery requires a separate authorized session scenario; this lane establishes only authenticated read-only inspection.
