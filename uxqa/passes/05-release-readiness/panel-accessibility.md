---
type: UX Research Panel
pass: 05-release-readiness
lens: accessibility
status: SOURCE_FREE_EVIDENCE_REVIEW
---

# Accessibility panel: pass 05

Pass 05 demonstrates a limited planning-draft review and canvas import, and exposes accessibility failures in the selected-node editor that earlier form-only scans did not reach. Two editable controls lack programmatic labels, Delete Node text fails its recorded contrast threshold, and a separate audit gold run retains a contrast violation. Functional completion is compatible with these failures. The panel does not certify accessibility, legal/tax accuracy, model correctness, settlement or release readiness.

This source-free review used the study, pass05 scenario/personas/rollup, all nine `runs/*/ux.json` files, the tenant lane summary and selected current screenshots. It used no application source, traces, sessions, raw network evidence, browser or provider calls. Private paths are references for authorized reviewers; screenshots, transcripts, the synthetic brief and identifiers are not reproduced here. No tests, code changes or environment operations were performed.

## Evidence accounting

The frozen rollup contains nine runs: eight UI journeys, all COMPLETED, and one separate probe excluded from UI outcomes. Coverage is 12/57 routes (21.1%); all three gold tasks passed once. Friction is 1.875 per UI journey versus the rollup's prior-pass 1.5: a 25% rise, failing the configured 10% tolerance. Time includes agent/automation time and is not a human completion estimate.

The total `axeViolations: 3` means **three rule instances across two unique rule IDs**, affecting four elements, not three different rules:

| Observed state | Rule / impact reported by axe | Elements | Evidence |
| --- | --- | --- | --- |
| `/actions`, selected-node inspector after import | `label`, critical | 2 | AI `ux.json`, second axe entry; tenant `lane-report.md`, accessibility item 1 |
| `/actions`, same inspector | `color-contrast`, serious | 1 | Same axe entry; lane accessibility item 2 and final note identify Delete Node and 3.34:1 |
| `/operator/audit`, gold destination scan | `color-contrast`, serious | 1 | `runs/qa-uxqa-operator--gold-operator-audit/ux.json` |

The AI intake's initial scan was clear. Its final selected-node scan was not. A separate operator-readiness scan of `/operator/audit` was clear; this discrepancy does not cancel the gold run's violation. Axe impact strings describe rule impact, not the panel's release-severity bands.

The tenant lane records one actual interview POST and no retry, despite generic harness `apiCalls: 0`. It reviewed a proposed draft and imported nine nodes/eight edges, selected a middle node and exposed editable fields. The initial imported fit view places the first node under the toolbar. The lane summary records a normal first-node click timeout outside `s.attempt`; therefore zero scored failedAttempts and friction 2 understate the observed selection detour. Selecting a middle node worked. This was not an API/source workaround, and no second model request was made.

## Observed accessibility barriers and severity hypotheses

Panel severity is impact × frequency × affected-segment breadth, each 1–3. Scores use the observed sample; one agent run is not population prevalence. An impact-3 barrier excluding one constrained segment is at least MEDIUM even when its product score is below 8. Human impact and prevalence remain hypotheses; the recorded scan failures are evidence, not merely missing coverage.

### A05-ACCESS-01 — Two editable inspector controls have no programmatic label

**Observation and hypothesis:** After a node is selected, the node-name input and JSON-configuration textarea are enabled/editable. The lane's permitted DOM summary reports no associated label, aria-label or aria-labelledby, and axe records two `label` failures. The screenshot shows visible Name and Config (JSON) text, so this is a programmatic association problem, not a claim that all visual instructions are absent. A screen-reader user may be unable to reliably distinguish and safely edit these essential fields.

- **Route/evidence:** `/actions`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json`, `imported-canvas-settled.png`, `imported-editable-canvas.png`; `flows/tenant-workflow-ai/lane-report.md`, accessibility item 1. Exact label mechanism is reported by that summary; this panel did not inspect source or independently query the DOM.
- **WCAG / heuristic / job:** WCAG 4.1.2 Name, Role, Value and 1.3.1 Info and Relationships are supported failure candidates. WCAG 3.3.2 requires separate contextual assessment because visible labels exist. Nielsen 6/5; job: identify and edit a selected node without guessing which field has focus.
- **Impact/frequency/breadth:** **3 / 1 / 1 = 3; MEDIUM** through the accessibility exclusion exception. Exclusion risk is for screen-reader users; no human task failure has been measured. Axe's critical rating does not by itself establish HIGH/BLOCKER.
- **Confidence:** High in the two scanned label failures and the summary's field identification; medium in essential editing exclusion, pending spoken-output/task confirmation.
- **Human confirmation/kill method:** Five screen-reader workflow designers identify each field, inspect a safe local draft and make a reversible edit without a pointer or visual coaching. Confirm impact if names/purpose are unavailable or ambiguous and users guess, edit the wrong field or cannot continue. Kill the exclusion hypothesis if meaningful names and relationships are actually exposed in the tested state and users edit reliably; a successful human workaround alone does not erase a reproduced programmatic failure.

### A05-ACCESS-02 — Delete Node text has insufficient contrast

**Observation and hypothesis:** The selected-node screenshot visibly contains the destructive action. The lane's detail summary records normal 12.8px text, foreground `#e6443a`, background `#492520`, ratio **3.34:1**, below the **4.5:1** requirement. Users with reduced contrast sensitivity may struggle to recognize the action. No deletion occurred, and no wrong action or human exclusion is claimed.

- **Route/evidence:** `/actions`; AI `ux.json` final note and post-import axe entry; `imported-canvas-settled.png`; tenant lane accessibility item 2. Color/font measurements are attributed to the permitted lane summary, not independently recomputed by this panel.
- **WCAG / heuristic / job:** Recorded WCAG **1.4.3 Contrast (Minimum)** failure. Nielsen 5/6; job: recognize the destructive node action before choosing an edit.
- **Impact/frequency/breadth:** **2 / 1 / 1 = 2; LOW by the strict panel rubric**, with a confirmed recorded contrast failure. The lane suggests MEDIUM; that higher candidate remains for triage/human impact assessment. Its one sampled state does not establish broader frequency or complete exclusion.
- **Confidence:** High for the recorded measurement and element; medium for material low-vision task impact.
- **Human confirmation/kill method:** Five low-vision workflow designers locate and explain the destructive action without activating it. Confirm impact if they miss or misidentify it under their normal viewing conditions. Kill the human-impact hypothesis if consistently recognized without material effort; separately close the WCAG failure only with valid adequate contrast in the same actionable state. Do not require participants to delete data to validate recognition.

### A05-ACCESS-03 — Audit gold scan retains an unresolved contrast failure

**Observation and hypothesis:** The gold `/operator/audit` scan reports one serious `color-contrast` node. Its settled screenshot still shows history loading and a different control presentation from the separate readiness screenshot, which has loaded records and a clear scan. The supplied `ux.json` does not identify the affected element or ratio. This is a retained automated failure requiring targeted confirmation; the evidence cannot assign it to a specific button or prove animation, loading, a disabled-state exemption or persistent colors caused it.

- **Route/evidence:** `/operator/audit`; `runs/qa-uxqa-operator--gold-operator-audit/ux.json`, `goal-result.png`; contrasting state in `runs/qa-uxqa-operator--operator-readiness/ux.json`, `audit-settled.png`.
- **WCAG / heuristic / job:** WCAG **1.4.3** reported failure, exact element/applicability unresolved. Nielsen 1/6; job: perceive audit controls and history status while navigating and loading.
- **Impact/frequency/breadth:** **2 / 1 / 1 = 2; LOW provisional panel severity**, not a clean scan. No goal failure occurred, so the gold-task impact-3/HIGH rule does not apply.
- **Confidence:** High in the raw recorded rule instance; low in its target, duration, cause and human impact. A later clear scan cannot kill the earlier observation.
- **Human confirmation/kill method:** Five low-vision operators inspect read-only entry/loading/loaded states on a safe fixture. In a separate authorized technical check, identify the exact node and measure its applicable contrast/state. Confirm material impact if users cannot perceive needed text while it is relevant. Kill a persistent-barrier hypothesis if adequate contrast and usability are demonstrated across those states; retain this pass's raw observation and document any substantiated false positive or exemption rather than silently deleting it.

## Other interaction and cognition hypotheses

### A05-ACCESS-04 — Fit view and toolbar may hinder first-node selection

- **Observation/evidence/route:** `/actions`; AI `imported-canvas-initial.png` and `imported-canvas-settled.png` show small nodes and a toolbar over the first node. Tenant lane accessibility item 3 records the unscored ordinary-click timeout followed by successful middle-node selection; `ux.json` records that later selection. The screenshot supports overlap, not a universal inability to select the first node.
- **Hypothesis/job:** Low-vision or motor-constrained authors may incur a detour to select a starting node. Nielsen 7/9. No WCAG failure established; keyboard operation and focus behavior were not tested in this graph. WCAG 2.1.1 and 1.4.4 are validation targets, not inferred failures. WCAG 2.4.11 is a later-version criterion and is not asserted here.
- **Impact/frequency/breadth:** **2 / 1 / 1 = 2, LOW**; medium confidence in a local pointer detour, low in constrained-segment exclusion.
- **Human confirmation/kill:** Five designers using magnification and/or motor accommodations select the first node with ordinary controls, then inspect its fields. Confirm if overlap or size repeatedly requires a workaround or prevents the task. Kill if the first node is readily discoverable/selectable using the provided controls. Record every attempt; do not equate this pass's zero failedAttempts with no difficulty.

### A05-ACCESS-05 — Dense scope and exact-configuration review may exceed working-memory capacity

- **Observation/evidence/route:** `/actions`; `planning-draft-review.png` shows long capability warnings, a node list, expanded JSON and acknowledgment copy; `before-single-submit.png` and tenant summary show extensive scope/consent instructions. These provide valuable limits. The agent reviewed and imported successfully; that does not measure human comprehension.
- **Hypothesis/job:** Designers with reading/working-memory constraints may acknowledge review without understanding that the graph is a generic planning scaffold and does not preserve/enforce the detailed business rules. Nielsen 6/8; no WCAG failure established from length, technical notation or successful agent reading alone.
- **Impact/frequency/breadth:** **1 / 1 / 1 = 1, LOW**; high confidence in presentation density, low in misunderstanding.
- **Human confirmation/kill:** Five workflow designers using cognitive accommodations explain what import does, what detailed decisions the node configurations retain, and what later execution could notify before acknowledging. Confirm if they repeatedly form consequential false beliefs or cannot identify how to preserve their decisions. Kill if they accurately understand the limits and proceed without material burden. No save/export/persistence outcome was tested here.

## Remaining coverage and human plan

The imported graph moves the previously untested objective into **partial functional/accessibility coverage**: one desktop pointer path, one selected middle node and one post-selection scan. It does not establish accessible graph traversal, keyboard selection/editing, focus order/visibility, status announcements, connection creation, magnification/reflow, narrow-screen controls or a usable nonvisual graph alternative. The successful account keyboard lane did not visit `/actions`; its four non-pointer activations and visible Settings focus outline cannot answer these editor questions. The recorded account zoom journey likewise did not visit `/actions`; its sampled verification screenshot remains readable but does not prove graph reflow.

Validate graph keyboard and assistive-technology operation against WCAG 2.1.1, 2.4.3, 2.4.7, 4.1.2 and 4.1.3; enlarged text/reflow against 1.4.4/1.4.10. These are coverage questions, not extra counted failures. Human confirmation is an unaided safe draft review/edit task with correct names, stable focus and understandable state changes; kill a barrier hypothesis only for states actually tested. Avoid executing, publishing or notifying as part of validation.

Integrate the five-per-affected-segment tasks above into the coordinator's existing human plan, reusing sessions where the participant and task genuinely cover the segment. Ensure it includes five screen-reader workflow designers, five low-vision designers/operators and five designers using cognitive accommodations; motor-accommodation coverage is additionally necessary for the selection hypothesis if not already represented. A participant may span segments, but do not count an unperformed session or infer a revised total from overlapping recommendations. **Zero human sessions have occurred in this pass.**

The refined brief produced no repeated operational blockers in this one response, according to the lane summary. That is progress over a prior partial path, not evidence that repeated-question cognition risks are universally resolved. The truthful import warning and completed pointer task do not remove the editor's label/contrast failures. No new HIGH is substantiated by this lens's strict scoring, but MEDIUM accessibility work and unresolved failures remain; the whole-pass new-high gate requires the coordinator's full triage. The friction gate already fails. Preserve the raw records and do not describe pass05 as a clean accessibility or passing release study.
