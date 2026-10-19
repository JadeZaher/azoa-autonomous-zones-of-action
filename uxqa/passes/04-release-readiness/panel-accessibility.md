---
type: UX Research Panel
pass: 04-release-readiness
lens: accessibility
status: SOURCE_FREE_EVIDENCE_REVIEW
---

# Accessibility panel: pass 04

The sampled account journeys provide positive evidence for keyboard navigation and enlarged content. This lens found no demonstrated WCAG failure and no evidence-backed new HIGH accessibility finding. That conclusion is limited to the recorded states. The AI planning form has two cognitive-accessibility hypotheses and substantial untested interaction states. Zero axe violations does not establish accessibility compliance or justify closing the whole pass's new-high gate.

This independent panel read the study, pass scenario/personas, rollup, all nine current `runs/*/ux.json` files, selected current screenshots and the AI lane summary. Pass 03's public synthesis/verdict supplied historical comparison only. No application source, traces, sessions, provider credentials, browser, network or model calls were used. Evidence paths below identify private artifacts for authorized reviewers; this report does not reproduce screenshots, transcripts, the fictional brief or identifiers.

## Observations and limits of positive evidence

The rollup records nine runs: eight UI journeys, seven COMPLETED and one AI PARTIAL, plus one excluded authorization probe. Coverage is 12/57 routes, friction is 1.5 per UI journey, and each of the three gold tasks passed once. These are agent QA measures, with zero human participants.

- In `runs/qa-uxqa-keyboard--keyboard-account-replay/ux.json`, the Wallets, Settings and Verify identity attempts succeeded on their first try. All four activation events are `pointer: false`; there are no workarounds. The Settings and identity-status screenshots visibly show a focus outline on the corresponding sidebar link. This supports the sampled navigation under WCAG 2.1.1 Keyboard and 2.4.7 Focus Visible; it does not prove all controls, focus order, screen-reader speech or dynamic focus behavior satisfy those criteria.
- In `runs/qa-uxqa-zoom--zoom-slow-account/ux.json`, the recorded `zoom: 2` and slow-network journey completed all four attempts. `navigation-open.png`, `settings.png` and `verify-identity.png` show enlarged text, a navigation drawer and readable sampled content. No clipping barrier or workaround was observed in these captures. Metadata and screenshots alone cannot certify browser zoom implementation, every lower-page control, WCAG 1.4.4 Resize Text or 1.4.10 Reflow.
- The 390-pixel recovery run reached a labeled operator sign-in form without an interaction failure. Its `goal-result.png` visibly contains the username/password labels and recovery explanation. Its 401/console entries record the signed-out request, not a demonstrated accessibility barrier. Similarly, the account lanes' identity-page console 404s did not produce a recorded failed attempt; these logs alone are insufficient to diagnose an inaccessible error state.
- All recorded pass 04 axe scans are empty, including two on `/actions`. The AI run was pointer-based at width 1366 and normal recorded zoom. Its telemetry's generic `TEXTAREA`, `INPUT` and generated-ID event labels are instrumentation labels, not proof of missing accessible names. The screenshots show visible form labels; actual name/description associations and spoken output remain untested here.

## Observed content barriers: hypotheses for human validation

Severity uses impact × frequency × segment breadth, each 1–3. Frequency and breadth reflect this evidence, not assumed prevalence. Neither item below is a confirmed WCAG failure or evidence that a disabled participant was excluded.

### A04-ACCESS-01 — Dense planning-scope choice may overload comprehension

**Hypothesis:** The planning-only radio's multi-line label combines graph purpose, unenforced controls, import behavior, later notifications and checkpoint limitations. Users with reading or working-memory constraints may select the option without retaining the distinctions needed for an informed choice. The explanation is visibly available; omission of the warning was not observed.

- **Route and evidence:** `/actions`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/assistant-reply-settled.png`, its `ux.json` scope-selection step, and `flows/tenant-workflow-ai/lane-report.md` scope/input section.
- **Heuristic / WCAG:** Nielsen 6, recognition rather than recall, and 8, minimalist design. No WCAG failure established. WCAG 3.3.2 Labels or Instructions is a relevant validation area; long copy alone does not fail it.
- **JTBD gap:** Choose a planning mode while understanding what import, checkpoint records and a later run can actually do.
- **Impact / frequency / breadth:** 1 / 1 / 1 = **1, LOW hypothesis**. One normal-size agent read it successfully; cognitive burden is anticipated for one affected segment, not observed human prevalence.
- **Confidence:** High that the label is dense; low that it causes misunderstanding or exclusion.
- **Human confirmation / kill method:** Run five sessions with workflow designers who use reading/working-memory accommodations. Without coaching, ask each to choose a scope and explain whether import executes, checkpoints enforce approval, or a later run may notify an external receiver. Confirm if the label repeatedly requires rereading or produces a consequential false belief. Kill if participants accurately explain the distinctions and proceed without material effort attributable to the label.

### A04-ACCESS-02 — Repeated supplied categories may impose avoidable recall and re-entry

**Hypothesis:** After a complete six-category intake, repeating broad asset/custody and allocation/recordkeeping questions may make users reconstruct what they already entered instead of identifying the precise missing detail. This can increase cognitive burden. The visible reply truthfully says no draft is available; no phantom draft or import success is claimed.

- **Route and evidence:** `/actions`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` final note and PARTIAL outcome, `assistant-reply-settled.png`, and `flows/tenant-workflow-ai/lane-report.md` response/outcome section. The permitted summary reports that these categories were supplied. The panel did not inspect the private complete brief independently, so the duplication interpretation partly depends on that summary.
- **Heuristic / WCAG:** Nielsen 6 and 9, recognition and recovery. No WCAG 2.1 AA criterion is shown to fail. Redundant Entry is a later WCAG criterion and is not asserted as a violation in this study.
- **JTBD gap:** Understand the remaining planning blocker without unnecessarily repeating prior answers.
- **Impact / frequency / breadth:** 2 / 1 / 1 = **2, LOW accessibility hypothesis**. This scores potential cognitive detour, not the overall service task's severity. One response is insufficient to estimate recurrence or affected segments.
- **Confidence:** Medium for apparently repeated categories; low for the size of human accessibility impact. Two other questions seek finer approval-evidence and settlement-handoff detail and should not be dismissed as duplicates.
- **Human confirmation / kill method:** In five sessions with workflow designers using cognitive accommodations, retain the original visible intake and ask users to explain and answer only what remains missing. Confirm if broad repetition causes material rereading, contradictory re-entry or abandonment. Kill if users readily identify the new detail requested and continue without reconstructing supplied categories. The one-request reservation prevented testing a second turn; a second-turn failure is not established.

## Coverage gaps: questions, not observed defects

These questions have no product-defect severity assigned. Their 1–3 values below describe provisional research risk only; they must not be counted as new HIGH defects.

| Question / route / evidence | Criteria and job to validate | Provisional impact / frequency / breadth; confidence | Human confirmation and kill method |
| --- | --- | --- | --- |
| **C04-ACCESS-01:** Can keyboard and screen-reader users complete the AI form, perceive pending/reply states and recover from withheld drafts? `/actions`; AI `ux.json` has `keyboardOnly: false`, pointer events, no graph, two clean axe scans. `assistant-reply-settled.png` shows reply content below the form. | WCAG 2.1.1, 2.4.3 Focus Order, 2.4.7, 3.3.2, 4.1.2 Name/Role/Value and 4.1.3 Status Messages are testing targets, not failures. Job: fill, consent, submit, locate the reply and understand the next action using assistive technology. Nielsen 1/9. | 3 / 1 / 1; high confidence that this lane did not exercise those interactions, low confidence of a barrier. | Five keyboard/screen-reader workflow designers use a separately authorized safe fixture. Confirm only if an operable control, meaningful status or recovery step is unavailable. Kill for the tested states if they complete unaided with correct announcements and stable focus. Import/canvas manipulation requires a separately available draft and remains outside current evidence. |
| **C04-ACCESS-02:** Does the AI form and its long response remain usable under enlarged text/reflow and mobile navigation? `/actions`; AI run is width 1366, zoom 1; the zoom run visits only `/overview`, `/wallets`, `/verify-identity`, `/settings`. | WCAG 1.4.4 and 1.4.10 are targets. Job: retain context between question, answer, scope, consent and reply without clipping or avoidable two-dimensional scrolling. Nielsen 6/7. | 3 / 1 / 1; high confidence of the coverage gap, low confidence of a defect. | Five low-vision workflow designers test 200% text and a 320 CSS-pixel equivalent reflow condition. Confirm if content/control loss prevents the job. Kill for tested states if all instructions and controls remain usable with a coherent reading sequence. Account zoom completion cannot answer this question for the AI form. |
| **C04-ACCESS-03:** Are initial transient controls and dynamic history states perceivable? `/settings`, `/operator/audit`; gold `goal-initial.png` versus `goal-result.png`, corresponding `ux.json`; pass03 `synthesis.md` and `verdict.md`. The pass04 audit gold settled screenshot still displays history loading. | WCAG 1.4.3 Contrast, 1.4.11 Non-text Contrast, 4.1.3 and Nielsen 1 are targets. Job: recognize available controls and know whether history is loading, empty or complete. Loading text is visible, but speech/announcement and final data state are not evidenced. | 2 / 1 / 1; high confidence that settled axe differs from full state coverage; low confidence of a current defect. | Five low-vision/screen-reader operators inspect navigation entry, loading, completion and empty/error states on a safe read-only fixture. Confirm with measured insufficient contrast in an actionable state or missing meaningful status perception; kill for sampled states with adequate measurements and usable announcements. Do not create an authenticated expiry/permission test claim from the anonymous redirect. |

## Cross-pass interpretation and disposition

Pass 03's recorded two contrast rule instances across three nodes remain historical failures. Pass 04 gold tasks retain initial captures and wait for finite entrance animations before settled capture/axe; its zero contrast results are evidence under that revised method. They neither erase pass 03 nor prove that initial controls, reduced-motion behavior or every loading state now meets contrast requirements. The paired current Settings images visibly differ in control presentation; screenshots do not supply a contrast ratio or a source-level cause. Infinite activity indicators are excluded from the finite-animation wait, consistent with the audit loading screenshot.

The AI run is PARTIAL with zero scored friction, showing why automated totals cannot substitute for goal and state analysis. The generic API count is zero while the permitted lane summary records one actual interview POST; zero does not mean no request occurred. No graph was offered, so graph review/import accessibility has no evidence here.

Carry the two LOW cognitive hypotheses and three coverage questions into human validation. There is no newly substantiated HIGH accessibility barrier from this evidence set, and no accessibility certification or release approval. Do not silently retire the earlier contrast record, extrapolate one successful account keyboard route to all 57 pages, or infer human success from agent speed. Agent literacy, patience, synthetic fixtures and accessibility-tree access may hide comprehension and assistive-technology barriers. No code changes, tests, environment operations or additional provider calls were performed by this panel.
