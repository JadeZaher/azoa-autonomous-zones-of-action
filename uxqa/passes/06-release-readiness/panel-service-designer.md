---
type: UX Research Panel
pass: 06-release-readiness
lens: service-designer
status: SOURCE_FREE_HYPOTHESES_FOR_VALIDATION
---

# Service designer panel: pass06

The manual authoring service supports adding nodes, local edits, visible canvas recovery and reversible history. Its first review handoff still breaks: Auto-layout leaves the starting node outside the graph viewport, three ordinary selection attempts fail, and the author must discover panning before inspection can continue. Undo/Redo restores the data but clears the active selection, requiring another context-recovery step. The observed task is honestly PARTIAL despite useful later recovery.

This lens frames handoffs between palette, canvas, inspector and history. It uses pass06 scenario/personas/rollup, the manual lane summary, geometry/focus/history reports and selected screenshots, public pass05 service/accessibility panels and `uxqa/human-validation.md`. This is a separate lens report authored by the same artifact reviewer, not an additional independently recruited researcher. No application source, source-triage files, traces, credentials, raw network, browser/API/model calls or environment changes were used. Private evidence is cited by path without reproducing screenshots, transcripts, identifiers or the fictional brief.

## Actual service path

The manual lane navigated Home → Dashboard → Actions → Builder, added nine ordinary Emit nodes through the palette and used Auto-layout. The graph remained **9 nodes / 0 edges**, with expected orphan/unreachable/no-terminal warnings. This is not the connected nine-node/eight-edge AI scaffold imported in pass05. No AI panel was opened and the method/path observer counted zero interview POSTs. It neither tests AI regeneration nor proves an imported draft was revalidated.

At desktop size, toolbar/flow separation is measured with at least9px clearance. Yet the disconnected row extends beyond both graph edges. Three wrapped first-node selection attempts fail; visible panning later exposes the first card. Normal card-center selections verify first, middle and last nodes. Name and valid JSON editing succeed; history restoration returns original values without changing9/0. A palette drag temporarily adds a tenth node at the requested point and Undo returns9/0. No save, publish, run, delete, transfer, verification, notification or authority operation occurs.

The rollup records seven completed UI journeys and one manual PARTIAL plus one excluded probe; all three original gold tasks pass and route coverage stays12/57. Friction6.625 fails the trend gate against1.875. Preserve the score, while disclosing that a different manual job replaced the paid AI path. Its47clicks,3failed attempts,2dead-click and7rage proxies are not estimates of human effort/frustration: nine deliberate rapid palette additions contribute to the rage proxy. A disclosed30-second automation read wait after Undo selection clearing is also not human hesitation.

## Service hypotheses

Ratings use impact × frequency × breadth, each1–3. Frequency reflects one observed lane and breadth denotes the hypothesized affected segments; neither is measured population prevalence. Each item is a validation hypothesis supported by the stated observed behavior, not a source diagnosis.

### SD06-1 — Automatic layout hands authors an incomplete review viewport

- **Evidence/route:** `/actions`; `flows/manual-planning-editor/desktop-geometry.json`, `lane-report.md`; `runs/qa-uxqa-ai-tenant--manual-planning-editor-regression/nine-nodes-auto-layout.png`. Flow is x502–1024; first card x158–248 and last x1278–1368 are clipped. The lane retains three initial failures and its abandonment marker; subsequent visible pans recover card access. Stable middle-card geometry after selection is positive evidence that that selection did not reset the viewport.
- **Hypothesis/job/heuristic:** The palette-to-layout handoff may cause authors to skip the start of a plan, believe a node is missing or spend effort discovering recovery before review. Job: inspect all authored nodes before passing work onward. Nielsen1,7,9. No specific WCAG failure is established by this pointer clipping episode.
- **Impact/frequency/breadth:** **2 / 1 / 2 = 4, LOW** for a costly detour affecting ordinary and constrained authors. Confidence high in clipping/failure/recovery, medium in detour cost, low in skipped review or human abandonment. Later recovery rules out claiming permanent inaccessibility in this desktop pointer path.
- **Human confirm/kill:** Within the existing prepared-author and low-vision-author slots, five each, ask users to inspect first/middle/last cards from the automatic layout. Confirm if they skip nodes, cannot locate the start or require costly coaching/workarounds. Kill if ordinary pan/zoom controls are readily discovered and permit complete review without material detour. Preserve the synthetic failed attempts regardless of human results.

### SD06-2 — History restores content but drops inspection context

- **Evidence/route:** `/actions`; `flows/manual-planning-editor/undo-redo-visible.json` records `selectionClearedByHistory: true` and both fields restored to original values after history actions. `undo-cleared-selection.png` shows the inspector's generic select-a-node instruction; `undo-redo-restored.png` shows the restored node after reselection. Lane summary discloses reselecting to inspect each restored state and the automation wait.
- **Hypothesis/job/heuristic:** After reversing an edit, authors may struggle to confirm what changed because the inspected node and its fields disappear from the active context. Job: safely undo/reapply a change and verify its exact result without reacquiring the item. Nielsen1,3,6,9. No observed data loss; undo worked. Focus return/announcement is an accessibility coverage question, not established by these history values.
- **Impact/frequency/breadth:** **2 / 1 / 2 = 4, LOW**. High confidence in selection clearing and restored data; medium in reorientation burden; low in human wrong-edit risk.
- **Human confirm/kill:** Use existing five prepared authors and five keyboard/screen-reader authors. Make a harmless synthetic edit, undo/redo and ask users to explain the current value/node. Confirm if they cannot verify the action, select the wrong item or need repeated recovery. Kill if context changes are clearly understood and an available independent route makes verification straightforward. Do not label a timed automation locator wait as human confusion.

### SD06-3 — Narrow panels may interrupt the palette-to-canvas-to-inspector loop

- **Evidence/route:** `/actions`; `narrow-geometry.json` plus `narrow683-toolbar-flow.png` show a63px-wide graph at683×900 while palette and inspector retain columns. `css-zoom2-geometry.json` separately records zero graph width under root CSS2 magnification; its screenshot shows constrained/offscreen presentation. These are distinct methods, neither native browser zoom. The toolbar wraps without overlapping the flow, but usable authoring across all panels was not demonstrated.
- **Hypothesis/job/heuristic:** A designer using a narrow window or magnification may retain controls and editing fields while losing the graph that connects them, breaking review and selection continuity. Job: move between available nodes, spatial plan and selected configuration. Nielsen2,7,9; WCAG1.4.4/1.4.10 require a separate applicable native-zoom/reflow assessment.
- **Impact/frequency/breadth:** **2 / 1 / 1 = 2, LOW provisional service severity**, high confidence in geometry, low confidence in human completion or available alternate arrangements. Escalate if an essential constrained-segment job is shown unavailable; do not infer that from CSS magnification alone.
- **Human confirm/kill:** In the existing five low-vision author slots, use real browser zoom and narrow windows; observe panel reading order, selecting an endpoint and verifying its configuration. Confirm if spatial access is lost or requires a material detour. Kill for tested states if a discoverable layout/navigation permits complete unaided review. Record actual zoom method and preserve both technical measurements.

### SD06-4 — Keyboard users may lose safe orientation at the destructive boundary

- **Evidence/route:** `/actions`; `delete-focus-comparison.json`, `keyboard-focus-path.json`, and paired `delete-rest.png`/`delete-keyboard-focus-settled.png`. Delete receives keyboard focus but its recorded settled paint properties match rest, with transparent outline and no shadow. Name/Config have programmatic labels and label-click focus; this improvement is distinct from visible focus.
- **Hypothesis/job/heuristic:** Authors moving from editable fields through checkboxes to Delete may not know which control would activate. Job: refine a plan safely through the inspector. Nielsen1,5; WCAG2.4.7 is the accessibility candidate. No Delete activation or harm occurred.
- **Impact/frequency/breadth:** **3 / 1 / 1 = 3, MEDIUM accessibility floor** for potential keyboard exclusion; high confidence in the local paint comparison, medium in task exclusion. This is not an independent additional failure beyond accessibility panel A06-1; deduplicate during synthesis.
- **Human confirm/kill:** Use the existing five keyboard/screen-reader author slots, including sighted keyboard operation, to identify focus through the inspector without activating Delete. Confirm if safe orientation fails; kill the exclusion hypothesis if a reliable visible cue and independent operation are demonstrated. Require technical focus verification regardless of a participant workaround.

## Handoffs still outside the evidence

Pass05's public panel raised preservation of detailed decisions when handing a generic planning scaffold to an implementer/reviewer, interpretation of planning versus enforcement, and scope/source comprehension. This manual lane supplies no new interview, export, persistence or external-recipient evidence. Its disconnected ordinary nodes cannot resolve questions about a connected AI scaffold. Manual9/0 is neither an implementation of approval/payment controls nor evidence that the previous9/8 import has failed.

The 75 proposed human slots remain **zero executed**. Map SD06-1/2 to prepared authors and the five keyboard/screen-reader authors, SD06-3 to the five low-vision authors, and SD06-4 to the keyboard author slots; each affected segment retains five sessions. Keep the five receiving implementer/reviewer slots on RQ-AI-HANDOFF and context preservation, the cognitive-accommodation slots on scope/review comprehension, and adviser-relevance questions on RQ-AI-ADVISER. These are tasks within the existing plan, not75 completed sessions or additional participant counts. Real interview research still requires its own provider budget; pass06 authorized zero calls.

The service supports local recovery and reversibility but leaves a PARTIAL initial review path and focus/layout concerns. No source cause, release approval, legal sufficiency, settlement, full accessibility or two-pass success is established. Keep raw scores and historical evidence intact; this lens makes no independently substantiated new HIGH claim.
