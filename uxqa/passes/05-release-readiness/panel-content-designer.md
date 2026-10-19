---
type: UX Research Panel
title: Pass 05 independent content-design panel
description: Source-free review of planning disclosure, editor labels and handoff comprehension.
---

# Content-design panel

Pass05 demonstrates review, acknowledgment and import of a generic planning scaffold into the editable canvas. It also exposes editor labeling defects that earlier form-only coverage could not detect. The copy repeatedly explains the limited scope, yet whether people can understand that scope and preserve their detailed decisions for a receiving implementer remains a research question.

This panel read only the study/human-validation context and permitted pass05 scenario, personas, report, private lane report, rendered intake/review/import text, rendered accessibility details, summaries extracted from every run JSON and selected current screenshots. It inspected no application source, executable flow, trace, secret or raw network/provider material and performed no browser, network or model action. Private paths below identify evidence for authorized review; this public artifact reproduces no raw brief, transcript, screenshot or tenant identifier.

## Recorded observations

- `ux-report.json` records eight UI journeys completed and one separate completed probe, nine raw runs total. All three gold predicates passed. Coverage is 12/57 pages (21.05%), with no unmatched paths. UI friction is **1.875**, up **25%** from pass04's 1.5; the 10% tolerance check fails. Navigation success does not override that result.
- The tenant lane records one actual interview submission, followed by a proposed private draft, expansion of its exact configuration disclosure, checked review acknowledgment and import. The canvas visibly changes from empty to **9 nodes / 8 edges**. The disclosure identifies nine Emit nodes joined sequentially by Control edges. Selecting a middle node exposes editable name/configuration fields. Nothing was saved, published or run; no economic execution or settlement was tested.
- Draft/status text correctly describes planning notes, manual/external settlement and absent approval/control enforcement. The acknowledgment explains canvas replacement, non-executing import and possible configured webhook notifications after a later explicit run. It is an observed acknowledgment action, not evidence of human comprehension.
- The intake contains detailed operational decisions, but the disclosed node payloads contain generic checkpoint markers and planning flags rather than those decisions. The interface explicitly says amounts, allocations, timing and approval rules are not embedded or enforced and must be preserved for separate implementation/review. The result must not be called an implemented payment workflow or a durable preservation of the brief.
- The initial AI-form axe scan is clear. After import and node selection, the `label` rule identifies **two unnamed editable controls**, despite visible `Name` and `Config (JSON)` text. The post-import `color-contrast` rule identifies **Delete Node** at **3.34:1**, below its recorded 4.5:1 threshold. The normal pointer lane's import success does not establish access to subsequent editing.
- The aggregate contains **three rule instances, two unique rule IDs and four affected nodes**: the two unlabeled editor fields, the Delete Node contrast target and a separate audit contrast node. The audit run preserves its rule/count but no target detail; this panel cannot identify that node or equate it with Delete Node. Automated “critical”/“serious” impact labels are distinct from the study's severity scores.
- `imported-canvas-initial.png` shows very small node text and the first node under floating toolbar controls. The lane report records a normal first-node selection timeout, then successful selection of a middle node. This is observed **unscored friction**: harness failedAttempts/deadClicks remain zero because the failed action was outside the attempt wrapper. This panel preserves those recorded metrics and does not manufacture a revised score.
- Adviser questions remain separate from planning readiness. Visible legal/tax wording supplies an issue checklist without a statutory threshold/rate or classification determination. US reading guides explicitly limit geographic and evidentiary scope, but their visible subjects remain digital/crypto assets for this fiat-only scenario. This panel assessed wording, not legal accuracy, current source content or compliance.

## Hypotheses for validation

Each finding is a human-outcome hypothesis anchored to the observations above. Impact × frequency × segment breadth uses 1–3 each; frequency and breadth are not inflated from one tenant lane. An objective access defect remains actionable evidence even if a small human sample succeeds. Code-authorized triage must establish any implementation root cause separately.

### CD05-1 — Visible editor labels may not identify the fields to assistive technology

**Hypothesis:** Authors using a screen reader may be unable to distinguish the selected node's name field from its JSON configuration field reliably, causing editing to stop or occur in the wrong control. Seeing nearby words is insufficient when those words are not programmatically associated.

**Evidence / route:** `/actions`; `flows/tenant-workflow-ai/axe-details-visible.json`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` and `imported-canvas-settled.png`. The recorded axe failures specify absent associated labels/accessible naming for both enabled editor controls. The screenshot independently shows their visible text. No screen-reader tenant journey was run.

**Nielsen / JTBD:** 6 recognition rather than recall; 5 error prevention. Job: identify and safely edit the intended checkpoint field using my usual technology. Programmatic-name review is also relevant to WCAG 1.3.1 and 4.1.2; specialist triage should establish the precise criterion mapping.

**Rating / confidence:** impact 3 (potential exclusion from editing), frequency 1, breadth 1 = 3; **MEDIUM under the accessibility impact-3 floor**. High confidence in the recorded missing-name defect; moderate confidence in the predicted task barrier. This is not a claim that a human participant has already been excluded.

**Human confirmation / kill:** Five keyboard/screen-reader authors using their normal technology should select a fictional checkpoint, announce both fields' purposes and make a harmless instructed edit without saving. Confirm if they cannot distinguish the controls or edit the wrong field. Kill/narrow the predicted exclusion if all five complete accurately unaided; that outcome does not repair the objective missing association. Account-keyboard success is not substitute evidence for this editor.

### CD05-2 — A generic imported scaffold may not carry enough context for a receiving implementer

**Hypothesis:** An author may expect their carefully entered decisions to accompany the imported graph. A reviewer receiving only the generic nodes may have to reconstruct who owns the controls, what evidence is required and which precise terms remain to implement. Honest warnings reduce overclaiming but do not themselves provide a usable handoff artifact.

**Evidence / route:** `/actions`; `flows/tenant-workflow-ai/submitted-intake-visible.json`, `planning-draft-review-visible.txt`, `imported-canvas-settled-visible.txt`; tenant `ux.json`. The detailed intake and expanded node payloads differ directly in retained specificity. The visible warning explicitly instructs external preservation. Save/export/persistence and a receiving-reviewer handoff were not exercised.

**Nielsen / JTBD:** 6 recognition rather than recall; 10 help/documentation. Job: preserve my decisions and give the next responsible person enough context for separate implementation and qualified review.

**Rating / confidence:** impact 2 (potential reconstruction detour), frequency 1, breadth 1 = **2, LOW**. High confidence that the disclosed scaffold omits the detailed decisions; moderate confidence in expected preservation and downstream cost. No observed data loss is claimed.

**Human confirmation / kill:** Five prepared authors should identify what survives import and prepare a fictional handoff. Separately, five receiving implementers/reviewers should identify required controls, responsible actors and unresolved work using only the handed-over material. Confirm if authors assume the details survive or recipients must reconstruct decisions. Kill/narrow if authors knowingly choose generic notes, deliberately preserve their brief elsewhere and recipients form an accurate plan without reconstruction. Test persistence only under a separately authorized future task.

### CD05-3 — Dense scope acknowledgment and raw JSON may not support an informed review

**Hypothesis:** Repeated long scope paragraphs plus an exact JSON disclosure may encourage a novice author to acknowledge the review without understanding what the graph contains. Names such as approval or settlement handoff can remain more memorable than the limits, even though the copy explicitly says those operations are not implemented.

**Evidence / route:** `/actions`; `flows/tenant-workflow-ai/planning-draft-review-visible.txt`; tenant `planning-draft-review.png` and `ux.json`. The exact configuration disclosure was expanded, acknowledgment checked and import completed. The title, node names and adjacent explanatory text consistently say planning/no funds movement. There is no observed deception or observed human misunderstanding.

**Nielsen / JTBD:** 2 match with the real world; 6 recognition rather than recall; 8 minimalist design. Job: decide whether this draft matches my intended planning purpose before replacing the canvas.

**Rating / confidence:** impact 2, frequency 1, breadth 1 = **2, LOW**. High confidence in content density; low-to-moderate confidence in comprehension cost.

**Human confirmation / kill:** Five first-time/plain-language authors and five authors using reading/working-memory accommodations, analyzed separately, should explain without coaching: what import replaces, whether any economic decisions are retained, whether approvals/payment are enforced, and whether a later run may notify configured recipients. Then ask them to identify one node's actual payload meaning. Confirm if they acknowledge while holding a materially incorrect expectation. Kill if all five in each segment explain the boundaries and payload accurately unaided. Preserve all material limitations in any future copy experiment; compare readable summaries rather than removing disclosures.

### CD05-4 — Small node labels and faint deletion text may obstruct review after a successful import

**Hypothesis:** Authors, particularly people with low vision, may struggle to read the default fitted node labels and destructive action. The covered first node can also interrupt the expected first-to-last review, even though other nodes are selectable.

**Evidence / route:** `/actions`; tenant `imported-canvas-initial.png`, `imported-canvas-settled.png`; `flows/tenant-workflow-ai/lane-report.md` and `axe-details-visible.json`. The screenshot supports small/covered content; the lane records one first-node timeout and successful middle-node selection. Delete Node's recorded contrast failure has a specific target and ratio. No deletion was attempted. The tiny node text has no measured contrast ratio, and the audit's unspecified contrast node is not used as evidence for this claim.

**Nielsen / JTBD:** 1 visibility; 6 recognition; 7 efficient use. Job: read and review every imported checkpoint and distinguish its safe editing actions. Delete Node's measured text contrast is relevant to WCAG 1.4.3.

**Rating / confidence:** impact 2 (observed selection detour/potential reading detour), frequency 1, breadth 1 = **2, LOW content-outcome score**. High confidence in the measured Delete Node defect and recorded timeout; moderate confidence in their effect on human review. The low research score does not waive the objective contrast finding or set a release gate.

**Human confirmation / kill:** Five low-vision authors using real 200% browser zoom and five ordinary desktop authors, kept separate, should read the first, middle and final node, select each and identify Delete Node without activating it. Confirm if review skips unreadable/covered nodes, requires repeated fit/pan recovery or the deletion action cannot be read. Kill/narrow the predicted review problem if all five per segment complete and read accurately without detour; objective contrast remains independently unresolved.

### CD05-5 — Honest US source limits do not ensure relevant adviser preparation

**Hypothesis:** Fiat-workflow authors may spend effort on digital/crypto-asset examples or overestimate their coverage when trying to prepare a local professional review. Explicit scope wording makes this a relevance/comprehension question, not evidence that the interface presents a legal conclusion.

**Evidence / route:** `/actions`; `flows/tenant-workflow-ai/submitted-intake-visible.json` and `planning-draft-review-visible.txt`. The rendered intake is fiat-only; guide labels cover digital assets, virtual currency models and crypto assets. The surrounding text calls them US examples/starting points and directs users to qualified local advisers. This panel did not open the links or verify their contents/currentness.

**Nielsen / JTBD:** 2 match with the real world; 10 help/documentation. Job: identify the right qualified review and relevant questions for my actual workflow/jurisdiction.

**Rating / confidence:** impact 1, frequency 1, breadth 1 = **1, LOW**. High confidence in differing subjects; low-to-moderate confidence in a harmful detour.

**Human confirmation / kill:** Five fiat-workflow authors should identify which examples fit their scenario, what jurisdictions are covered and what local professional review remains. Confirm if they treat the examples as tailored coverage or cannot identify a relevant next step. Kill if all five independently recognize their limits and choose an appropriate review path without detour. Qualified professionals must assess substantive relevance separately; participant confidence cannot establish legal/tax sufficiency.

## Interpretation limits and next evidence

The actual pass05 import is preserved as completed for its limited task. Pass04 remains partial; refining the synthetic input and obtaining one new successful response does not retroactively prove that its prior follow-ups were necessary, unnecessary or generally fixed. The imported object is a generic planning scaffold, with no evidence of saved persistence, implemented economic decisions, approval enforcement, payouts, settlement or notifications.

The mobile, keyboard and zoom account journeys completed their recorded navigation tasks. They did not test the AI review/editor with those technologies. No human session has occurred; follow the separate segment recruitment and consent plan in `uxqa/human-validation.md`. The proposed sessions above require their own guarded authorization and call budgets, and may not consume another provider request under this completed lane.

Metrics remain unchanged: first-node timeout is unscored evidence, aggregate axe counts are rule instances rather than four rules, and audit contrast remains unidentified. Successful imports, gold predicates and truthful disclaimers do not establish complete accessibility, human comprehension, professional legal/tax review, two passing study passes or release approval.
