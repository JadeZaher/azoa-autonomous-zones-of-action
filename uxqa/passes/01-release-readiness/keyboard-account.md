---
type: UX Lane Report
title: Keyboard-only wallet, settings and identity navigation
---

Persona: `qa-uxqa-keyboard`, seeded ordinary member, keyboard-only and accessibility-tree perception, attempt budget 3. Target: `http://localhost:3200`. The journey used Tab, Shift+Tab and Enter for all interaction. Screenshots were captured as evidence but not used to guide the persona. No pointer clicks, source inspection, direct API calls, settings saves, provider actions or verification starts occurred.

## Outcome and reproducibility

COMPLETED: public page -> Dashboard -> Overview -> Wallets -> Settings -> Verify identity. The three recorded subgoal attempts each succeeded on the first try. The journey stopped after reading identity state and called `finish()`.

Commands executed from the workspace:

```powershell
$env:UXQA_PLUGIN_LIB='C:/Users/atooz/.codex/plugins/cache/local/ux-qa-research/0.1.0/lib'
$env:UXQA_CONTAINER_RUNTIME='podman'
$env:UXQA_E2E_ROOT='C:/Users/atooz/Programming/Projects/oasis-sleek/.worktrees/node-admin-release/frontend/e2e'
node uxqa/adapter/adapter.mjs guard
node uxqa/passes/01-release-readiness/flows/keyboard-account/driver.mjs
```

The read-only adapter guard executed with host escalation returned `OK azoa-e2e Development fake-vendors loopback-bindings` immediately before this lane. The driver records executed action input to `flows/keyboard-account/replay.mjs`. The saved replay uses a separate replay task name and was not re-executed.

## Observations

| Goal / expectation | Actual accessibility-tree result | Evidence / recovery |
| --- | --- | --- |
| Enter my dashboard | Fifth Tab reached “DASHBOARD” after the seeded session hydrated; Enter reached Overview. The first snapshot taken before hydration had shown LOGIN. | `starting-page.png`, `after-login-link.png`, private trace. No credential entry needed. |
| Find wallets | Eleven more Tabs reached Wallets (including a first focus snapshot spanning the sidebar). The wallet page announced Wallets, 0 total/external/platform wallets, “No wallets yet”, and “No balances”. | `wallets.png`, `ux.json` open-Wallets attempt. Reached unaided. No wallet creation or connection. |
| Find account/integration settings | Sixteen further Tabs reached Settings. Tree contained Session Auth Active, SDK Configuration, webhook registration, tenant permissions and delivery controls. | `settings.png`, settings attempt. Read only; no controls changed. |
| Understand identity status | Twenty Shift+Tabs from Settings reached Verify identity. Tree said “You have not started a verification yet”, explained adding funds/cashing out need verification, and exposed Start verification plus disabled Refresh result. | `identity-status.png`, identity attempt. Status understood from text; no verification initiated. |
| Inspect overview status | Overview displayed “Chain info failed: TypeError: Failed to fetch” twice. | `after-login-link.png`, trace. Source/cause not investigated; console access-denied errors make environment restrictions a possible explanation. |

The route remained keyboard navigable throughout this path; no focus trap was encountered. Reaching common destinations involved 52 Tab/Shift+Tab presses and four Enter activations across this chosen path (5 + 11 + 16 + 20). The accessibility tree showed Verify identity under Money while KYC Provider appears under Identity. This is a navigation/copy observation, not a confirmed classification defect.

Axe reported `color-contrast`, impact `serious`, on 9 nodes on `/`, 8 on `/wallets`, 7 on `/settings`, and 6 on `/verify-identity`. No other rules were returned by these scans. This is not a full accessibility audit; no claim about every focus indicator, control or route is made.

## Counts and limits

`runs/qa-uxqa-keyboard--keyboard-account/ux.json` records COMPLETED, 90 seconds, 4 keyboard-generated click events, 0 pointer click events, 0 dead/rage clicks, 0 inputs/validation errors, 0 failed attempts, 0 workarounds and no abandonment. Six navigation events include five distinct paths and one revisit. Friction score is 14. Four axe rule occurrences represent one rule on four scanned pages, not four distinct rules.

The harness counted 0 local `/api/` calls and 0 local HTTP errors. Five console errors comprise two access-denied resource messages on `/`, two on `/overview`, and one 404 resource message on `/verify-identity`. The UI still rendered the stated identity status; the 404's source was not inspected. API counting is scoped to the configured local `/api/` prefixes and is not total backend-request coverage.

Primary evidence: `runs/qa-uxqa-keyboard--keyboard-account/ux.json`, `trace.zip`, `starting-page.png`, `after-login-link.png`, `wallets.png`, `settings.png`, `identity-status.png`. The trace remains private and screenshots may expose marked fixture account metadata; publish only after review.

## Hypotheses for human validation

The large sidebar may add substantial keyboard effort for routine account tasks. Validate with five keyboard-only developers finding Wallets, Settings and identity state; measure navigation keys, path changes and task completion. The split between Identity/KYC Provider and Money/Verify identity may confuse people seeking their own verification state. Ask the same five participants where they expect personal verification to live before exposing navigation. These are hypotheses, not code-confirmed defects.

No test, lint, build, administrative authorization or complete route-coverage gate was executed in this lane.
`node --check uxqa/passes/01-release-readiness/flows/keyboard-account/replay.mjs` completed with exit 0. This is syntax validation only.
