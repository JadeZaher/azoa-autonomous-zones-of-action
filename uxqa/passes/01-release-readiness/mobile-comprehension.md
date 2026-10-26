---
type: UX Lane Report
title: Mobile comprehension at 390 pixels
---

Persona: `qa-uxqa-mobile`, anonymous newcomer, visible text only, width 390, attempt budget 2. Target: `http://localhost:3200`. No application source, direct API calls, credentials, provider actions or mutations were used. These are observations from an agent persona, not human research findings.

## Outcome and reproducibility

Corrected journey completed: public offer -> ordinary sign in -> operator sign in -> study-supplied anonymous `/operator/audit` visit -> operator sign in. Both visible navigation attempts succeeded on the first try. No password was entered and no recovery notification was requested. Every created journey called `finish()` and has a private trace.

Commands executed from the workspace:

```powershell
$env:UXQA_PLUGIN_LIB='C:/Users/atooz/.codex/plugins/cache/local/ux-qa-research/0.1.0/lib'
$env:UXQA_CONTAINER_RUNTIME='podman'
$env:UXQA_E2E_ROOT='C:/Users/atooz/Programming/Projects/oasis-sleek/.worktrees/node-admin-release/frontend/e2e'
node uxqa/adapter/adapter.mjs guard
node uxqa/passes/01-release-readiness/flows/mobile-comprehension/driver.mjs
node uxqa/passes/01-release-readiness/flows/mobile-comprehension/driver-corrected.mjs
```

The first sandboxed guard returned `REFUSED IN_TARGET_GUARD`. The same read-only guard run with host escalation returned `OK azoa-e2e Development fake-vendors loopback-bindings` before each actual mobile journey. A preliminary Node REPL import failed `process is not defined` before it could create a journey. No environment or guard changes were made.

The driver records each executed UI action into `flows/mobile-comprehension/replay.mjs` and `replay-corrected.mjs`; interactive driver input was the recorded replay body. The corrected replay uses a separate replay task name and does not overwrite the original trace. Replays were saved, not re-executed.

## Observations

| Goal / expectation | Actual visible result | Evidence / recovery |
| --- | --- | --- |
| Understand the offer | Opening text says AZOA models financial workflows, circular economies, operational processes and grant programs as living graphs of action. It introduces actions, holons, rails and bundles later in the long page. | Corrected `public-home.png`; `ux.json` first step; private trace. Meaning was read, not tested with a human comprehension question. |
| Find sign in | Mobile visible body text contained LOGIN after the long explanatory sections. The corrected visible-text selector found one visible LOGIN and activated it. | Corrected `member-login.png`; first attempt succeeded. This lane did not operate the header icon because perception/actions used visible text. |
| Understand ordinary vs operator sign in | Ordinary sign in says account recovery is not configured on this node and offers “Running this node? Open operator sign in”. | Corrected `member-login.png`; recovered unaided by that text link. |
| Recover from signed-out operator visit | `/operator/audit` redirected to `/operator/login`. The page says credentials are configured by this node's deployer and lost credentials are reset through the deployment's secret store. “Ordinary sign in” is available. | Corrected `operator-login.png`, `signed-out-audit-recovery.png`, private trace. Form was reached; authentication/secret-store recovery was not exercised. |

Axe reported `color-contrast`, impact `serious`, on 10 nodes on `/`, 3 on `/login`, and 7 on `/operator/login` (operator login was scanned twice). These are automated findings; affected nodes need code/visual confirmation in triage. No claim of complete accessibility compliance is made.

## Counts and failed attempts

| Run | Outcome | Seconds | Clicks | Failed attempts | Workarounds | Distinct paths | API calls counted | Axe rule occurrences | Friction |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `qa-uxqa-mobile--mobile-comprehension` | ABANDONED: automation ambiguity | 32 | 0 | 2 | 0 | 1 | 0 | 2 | 22 |
| `qa-uxqa-mobile--mobile-comprehension-corrected` | COMPLETED | 30 | 2 | 0 | 0 | 4 | 1 | 4 | 12 |

The initial locator matched both LOGIN elements and Playwright strictness rejected it before any click. The two failed attempts and ABANDONED marker are preserved in the initial `ux.json`; they are harness evidence and must not be treated as human/product abandonment. The correction filters the same visible text by visibility, with no source/API shortcut.

The completed run has zero dead clicks, rage clicks, inputs and validation errors. Seven navigation events include four distinct paths and three revisits. It recorded one HTTP error: the anonymous `/api/operator/session` GET returned 401 during signed-out recovery. Three console errors comprise two `ERR_NETWORK_ACCESS_DENIED` resource messages on `/` and the expected 401 resource message. These counts do not establish a backend defect. API counting is restricted to study-configured local `/api/` requests and is not total backend coverage.

## Evidence paths

Primary directory: `runs/qa-uxqa-mobile--mobile-comprehension-corrected/`, containing `ux.json`, `trace.zip`, `public-home.png`, `member-login.png`, `operator-login.png`, `signed-out-audit-recovery.png`. Initial directory: `runs/qa-uxqa-mobile--mobile-comprehension/`, containing the preserved failed-run `ux.json`, `trace.zip` and screenshots. Traces remain private because they can contain authentication state.

## Hypotheses for human validation

The long public explanation and a sign-in link late in mobile visible text may increase discovery effort for newcomers. Validate with five low-literacy mobile participants finding sign in unaided at 390 pixels; record time, path and abandonments. Operator recovery copy may still require technical knowledge of a secret store; validate with five interrupted operators explaining the next action in their own words. Neither hypothesis is confirmed by this persona run.

No test, lint, build, auth capability or complete route-coverage claim was executed in this lane.
`node --check uxqa/passes/01-release-readiness/flows/mobile-comprehension/replay-corrected.mjs` completed with exit 0. This is syntax validation only.
