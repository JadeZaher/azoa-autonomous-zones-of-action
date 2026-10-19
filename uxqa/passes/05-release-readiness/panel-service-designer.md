---
type: UX Research Panel
pass: 05-release-readiness
lens: service-designer
status: HYPOTHESES_FOR_VALIDATION
---

# Service designer panel

The interview-to-canvas handoff is now observed: one consented interview request produced a reviewable planning draft, the author acknowledged its limits, imported it, and selected an editable node. The canvas contained nine Emit nodes and eight Control edges. The result is a generic planning scaffold; successful import does not establish that detailed business decisions survive into a useful external handoff or that operational controls exist.

This source-free panel used the study, pass05 scenario/personas/rollup, all run summaries, the tenant lane report, rendered review/canvas evidence and the imported-canvas screenshot. No application source, traces, sessions, credentials, raw network responses, browser or provider calls were used. Evidence paths below identify private review artifacts; full briefs, transcripts, identifiers and screenshots are not republished.

## Observed outcomes and limits

`ux-report.json` records nine raw runs: eight completed UI journeys and one separate probe. All three navigation gold tasks passed once; page coverage is 12/57 (21.1%). Friction is 1.875 per UI journey, 25% above the recorded pass04 value of 1.5, exceeding the 10% tolerance. The three aggregate axe rule instances comprise two on the newly reached selected-node editor and one on operator audit. Completion therefore does not mean a clean experience. The newly observed editor also expands the tested state; the rollup does not establish a source regression or that every visitor meets these barriers.

`flows/tenant-workflow-ai/lane-report.md` and `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` substantiate limited planning review/import completion, without save, publish, run or operational action. Generic API instrumentation reports zero calls while the lane's method/path-only observer records one interview POST; zero must not be interpreted as no provider use. A first-node selection timeout occurred outside scored attempts, so zero failed attempts understates that observed detour. Agent/model elapsed time is not a human completion estimate.

The refined intake supplied approval evidence, independent dispute ownership, external settlement responsibility and reconciliation evidence. This request did not reproduce broad repeated blockers. Because the brief changed and only one request was observed, this does not establish general interview reliability or prove that every earlier clarification was unnecessary.

## Ranked hypotheses

Scores are impact × frequency × breadth, each 1–3. Frequency 1 means one observed journey; breadth estimates affected segments, not population prevalence. Human validation below is proposed work: zero human sessions occurred.

### SD05-1 — Import can succeed while accessible editing remains an incomplete service handoff

**Hypothesis:** A designer who relies on programmatic field names may receive the graph but be unable to confidently inspect or revise the selected checkpoint, interrupting the author-to-implementer preparation step.

**Evidence:** `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` records no initial form violations, followed by `label` on two selected-node controls and `color-contrast` on one element. `flows/tenant-workflow-ai/lane-report.md` identifies enabled editable name/configuration controls without programmatic labels and Delete Node text at 3.34:1. `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/imported-canvas-settled.png` shows the selected settlement checkpoint and editor. The import used a pointer; no screen-reader editor journey was performed. The separate keyboard account journey did not visit this state.

**Route / Nielsen / JTBD:** `/actions` → imported canvas → selected node; Nielsen 6 (recognition), 7 (efficiency), 9 (recovery). Job: inspect and refine the imported plan independently before transferring it to another responsible actor.

**Rating:** impact 3, frequency 1, breadth 1 = **3; MEDIUM floor for a potential accessibility exclusion**. Confidence: high in recorded label/contrast observations, medium in the hypothesized task barrier, low in prevalence. Actual exclusion is not demonstrated by this pointer run.

**Human confirm / kill:** Five keyboard/screen-reader workflow designers should import a synthetic plan, identify both editor controls, inspect the settlement checkpoint and make/revert an authorized harmless edit. Confirm if unnamed fields prevent reliable independent completion; kill the task-exclusion hypothesis if all can identify and edit reliably through an available accessible route. A successful session would not erase the recorded labeling/contrast findings; specialist triage still evaluates those separately.

### SD05-2 — The planning artifact may shed decisions needed by its next recipient

**Hypothesis:** A detailed interview followed by a generic graph may leave implementers or reviewers without the amounts, role ownership, evidence requirements and exception rules that made the plan ready to import.

**Evidence:** `flows/tenant-workflow-ai/lane-report.md` describes detailed six-category intake but nine generic checkpoint payloads. `flows/tenant-workflow-ai/planning-draft-review-visible.txt` discloses the exact configurations: checkpoint/scope and review/funds flags, without the detailed business rules. The imported settlement-node configuration is visible in `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/imported-canvas-settled.png`. Rendered review copy explicitly says amounts, allocations, timing and approval rules are neither embedded nor enforced and must be preserved for separate implementation/review. It also warns that the interview is discarded on page close or account change. Save, export, re-entry and recipient use were not tested; permanent loss is unproved.

**Route / Nielsen / JTBD:** `/actions` → interview → draft review → canvas; Nielsen 2 (real-world match), 6 (recognition), 7 (efficiency). Job: give another responsible person a coherent plan and the decisions needed to implement or challenge it.

**Classification / rating:** **Service capability and context-preservation research question**, with truthful disclosure; impact 2, frequency 1, breadth 2 = **4 / LOW**. Confidence: high that the displayed node payloads are generic, low that users lose context or lack a workable companion-document method. This is not evidence of deception or an observed external handoff failure.

**Human confirm / kill:** Five tenant authors and five receiving implementers/reviewers should transfer an imported synthetic plan using the workflow they would naturally choose, including any available companion record. Confirm if recipients must re-interview the author or reconstruct important decisions. Kill if the normal handoff reliably preserves and connects the brief, open questions and checkpoint responsibilities. Include an interruption/re-entry exercise before asserting a persistence defect.

### SD05-3 — A sequential checkpoint diagram may be mistaken for completed operational control

**Hypothesis:** Some authors or downstream recipients may interpret linked approval, dispute and settlement checkpoints as an implemented business process, despite explicit planning-only warnings.

**Evidence:** The review and imported-canvas evidence name all nine nodes as records or external handoff notes; the configuration uses planning scope, no-funds and external-review flags. `flows/tenant-workflow-ai/lane-report.md` records that import stayed disabled until acknowledgment. `flows/tenant-workflow-ai/imported-canvas-settled-visible.txt` distinguishes import from execution and warns that later explicit runs can enqueue configured tenant webhook notifications, while checkpoints neither wait for nor prove approval. The settled screenshot shows a connected sequential graph and the manual builder's Create Action control. No participant misunderstanding or external recipient interpretation was observed.

**Route / Nielsen / JTBD:** `/actions` → review → canvas; Nielsen 1 (status), 2 (real-world match), 5 (error prevention). Job: know what the artifact can do, what remains to implement, and who supplies review and settlement evidence.

**Classification / rating:** **Expectation hypothesis**, not a false-promise finding. Impact 2, frequency 1, breadth 2 = **4 / LOW**. Confidence: high that scope warnings and acknowledgment exist, low that the diagram defeats them.

**Human confirm / kill:** Five process authors and five receiving operators/implementers should explain the artifact before and after import: what happens now, what a later run may notify, who approves, and what proves payment. Confirm if they infer automatic enforcement or treat a recorded checkpoint as proof. Kill if they consistently distinguish planning notes, external work, notification and evidence. Do not execute notifications or payments to test comprehension.

### SD05-4 — Default canvas framing may obstruct review of the beginning of the plan

**Hypothesis:** Small nodes and toolbar overlap make the imported plan costly to inspect from its entry point, encouraging a reviewer to skip context or start in the middle.

**Evidence:** `flows/tenant-workflow-ai/lane-report.md` records a normal first-node click timing out, followed by successful middle-node selection. `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/imported-canvas-settled.png` shows the first node under floating toolbar controls and very small node text at the 1366 × 900 viewport. `ux.json` records successful settlement-node selection but no scored failed attempt; this is a disclosed UI detour, not measured abandonment or an API workaround.

**Route / Nielsen / JTBD:** `/actions` → imported canvas; Nielsen 1 (status), 7 (efficiency). Job: review the whole plan in order before editing or handing it off.

**Rating:** impact 1, frequency 1, breadth 2 = **2 / LOW**. Confidence: high in the observed overlap/detour, medium in review cost, low in recurrence.

**Human confirm / kill:** Five desktop workflow designers should inspect the entry checkpoint and follow the full chain from the default imported view. Confirm if overlap/small text causes skipped nodes, detours or failed selection. Kill if normal pan/zoom/fit controls are readily discovered and yield reliable entry-node review without meaningful cost. Test constrained authoring separately; account-navigation success at zoom does not establish canvas usability.

## Authority and recovery boundary

Pass05 run summaries show successful member navigation to settings/identity status, visible ordinary-to-operator sign-in navigation, and signed-out audit redirection. The 401 events during recovery do not themselves prove a service break. Operator inspection reached overview, audit, tenants and providers; no authenticated expiry or permission denial was induced. These observations establish read-only discovery and recovery entrance, not deployer credential recovery, revocation, cross-role resolution or provider recovery. The separate anonymous probe adds no UI success evidence.

Functional import, truthful limits and preserved publication/run boundaries are positive observations. External implementation, adviser review, context persistence and actual service handoffs remain untested. This panel offers no release, legal/compliance, settlement or complete accessibility certification.
