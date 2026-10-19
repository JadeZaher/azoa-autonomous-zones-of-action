---
type: UX Research Panel
title: Pass06 trust and behavioural panel
description: Artifact-only hypotheses about confidence, recovery and consequential control boundaries in the manual editor.
---

# Trust and behavioural lens

The recorded editor supports reversible changes without executing anything, but uncertainty about focus, selection and graph framing can weaken confidence in what a person is about to change. Those are hypotheses about perceived control, not demonstrated human fear, harm or intent.

The same single analyst wrote this and the separate content-design report as **two lens passes, not independent analysts or human evidence**. This report uses only allowed pass06 UX scenario/personas/report, manual lane report and rendered geometry/focus/Undo/contrast/appearance evidence, selected screenshots viewed through `view_image`, and the existing human-validation plan. No application source, source-triage material, raw trace, secret, browser/API/provider/model material or live action was used. Private evidence paths are review references; no screenshot, raw transcript or identifiers are published.

## Recorded observations and counterevidence

The new task manually creates **nine ordinary Emit nodes, zero edges and zero interview calls**. It is not a regenerated or reimported AI graph. No save, publish, run, Delete activation, notification, funds movement or authority change occurred. Publish remained unchecked. A temporary tenth palette-dropped node was removed with Undo; final counts returned to 9/0 and edited Name/config values returned exactly to their originals. These are positive bounded recovery observations.

Toolbar/flow separation is supported at desktop and narrow size. However, initial auto-layout clips the graph: three wrapped attempts to select the first node fail, preserving an abandonment marker and **PARTIAL**. Visible panning later enables normal first/middle/last selection; selecting the middle card preserves its measured viewport. History restores values but clears selection, briefly returning the inspector to generic instructions. This supports reversible state changes, not uninterrupted clarity.

Four manual axe scans have zero findings and the checked fields have associated accessible names. Dark Delete text contrast passes, but its settled resting and focused appearance has identical measured foreground/background/border/outline/shadow/filter/transform, with a transparent outline. The keyboard target is focused; no obvious distinct Delete focus cue is visible in the screenshot. Name/Config samples alone do not establish that every control lacks a cue. Actual light-theme behavior remains unverified because browser light preference left the interface dark.

At 683×900 the graph is 63px wide. Separate root CSS zoom2 produces zero measured width; neither is native browser zoom. This is bounded layout evidence, not a claim that a particular assistive-technology user failed.

The pass aggregate remains seven completed UI runs, one partial manual run and a separate probe; gold predicates 3/3, page coverage 12/57 and friction 6.625 with the trend gate false. The manual score of 41 includes planned rapid palette additions in a seven-rage-click proxy. Those clicks do not establish human impatience. The score, three failed attempts, two dead clicks and PARTIAL are frozen; this panel neither rescales them nor compares this manual job as an identical AI task.

## Hypotheses for validation

Impact × observed frequency × segment breadth are scored 1–3. A single synthetic lane does not justify wider frequency. Human confirmation can test predicted consequences; it cannot retrospectively remove the observations.

### TB06-1 — Indistinct focus at Delete can weaken safe keyboard control

**Hypothesis:** A sighted keyboard author who cannot identify focus on Delete Node may stop editing or activate an unintended control. Clear destructive text does not itself reveal the current keyboard target.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/delete-focus-comparison.json`, `keyboard-focus-path.json`, `delete-contrast-states.json`; `runs/qa-uxqa-ai-tenant--manual-planning-editor-regression/delete-keyboard-focus-settled.png` and `delete-rest.png`. Focus is recorded without a distinct settled visual style. No Delete activation or content loss occurred. The concern warrants visual-focus review relevant to WCAG 2.4.7; this lens makes no conformance ruling.

**Nielsen / JTBD:** 1 visible status; 3 control/freedom; 5 error prevention. Job: know which control will respond before pressing a key, especially near deletion.

**Rating / confidence:** potential impact 3 × 1 × 1 = 3; **MEDIUM under the accessibility impact-3 floor**. High confidence in the saved rest/focus comparison; moderate confidence in perceived-control/exclusion consequences. Harm or exclusion has not been observed in a human session.

**Human confirmation / kill:** In the existing five keyboard/screen-reader author slots, have sighted keyboard participants identify focus as they traverse fields, checkboxes and Delete, without activating consequential actions. Confirm if they cannot name the focused control, lose position or stop because its effect is uncertain. Kill/narrow if all five identify every target accurately unaided. A workaround does not replace independent paint/focus verification, and nonvisual users' successful announcements do not prove sighted focus visibility.

### TB06-2 — Selection clearing after Undo can obscure trustworthy recovery

**Hypothesis:** Authors may be less confident that Undo worked when the inspector becomes empty at the same moment. Repeated corrective actions could then be driven by uncertain feedback rather than by a failed history operation.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/undo-redo-visible.json`, `undo-cleared-selection-visible.txt`; manual run `undo-cleared-selection.png` and `undo-redo-restored.png`. The recorded restoration is correct; reselection is needed to verify it. The extra read wait was an automation detour, not a measured human pause or abandonment.

**Nielsen / JTBD:** 1 visible status; 3 control/freedom; 6 recognition. Job: reverse my latest edit and confidently verify the intended result without affecting another node.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in state/selection behavior; moderate confidence in uncertainty. No data loss is claimed.

**Human confirmation / kill:** Within five prepared author slots, observe reversible edit/Undo/Redo and ask for an unaided account of the current state before reselection. Confirm if they repeat history commands or re-enter content due to believing the restoration failed. Kill if all five understand the selection change and verify the restored value without unintended additional edits. Test trust through behavior/explanation, not a leading “do you trust this?” question.

### TB06-3 — Offscreen graph content may be mistaken for missing work

**Hypothesis:** Immediately after Auto-layout, clipped first/final cards may make authors think nodes were omitted or removed, reducing confidence in their manual construction even though the count remains nine and visible pan/zoom can recover them.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/desktop-geometry.json`, `visible-zoom.json`, `lane-report.md`; manual run `nine-nodes-auto-layout.png`, `first-node-visible-pan.png`, `last-card-selection-verified.png`. Three initial failures and later pan recovery are both preserved. A minimap and zoom controls provide counterevidence to a claim that no recovery path exists.

**Nielsen / JTBD:** 1 visibility; 6 recognition; 7 efficiency. Job: confirm that all my nodes still exist and review the correct card after arranging the graph.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in clipping and recovery; low-to-moderate confidence in a human inference of lost work. Intentional repeated palette clicks do not support this hypothesis by themselves.

**Human confirmation / kill:** In existing five first-time author sessions, ask participants to account for and inspect first/middle/final nodes immediately after layout, then explain any apparent missing content. Confirm if they add duplicates, infer data loss or cannot discover pan/zoom. Kill/narrow if all five reconcile the count/view and recover every node unaided without duplication.

### TB06-4 — Structural warnings beside Create Action may leave readiness ambiguous

**Hypothesis:** A novice may be unsure whether the visible Create Action control means the disconnected draft is ready, or whether orphan/unreachable/Terminal warnings are mandatory before creation. Ambiguous readiness could produce needless hesitation or premature creation intent.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/after-wrapped-selection-visible.txt`; manual run `undo-cleared-selection.png` and `narrow683-toolbar-flow.png`. The warnings correctly describe the recorded 9/0 graph and one directly explains marking Terminal. Create Action was visible but never activated; this report cannot claim whether it accepts, rejects, saves, validates or executes that state.

**Nielsen / JTBD:** 1 status; 5 error prevention; 9 recovery. Job: know what remains incomplete and what a next action will do before committing anything.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in adjacency/state; low confidence in human readiness confusion. The control's presence is not a false-success claim on its own.

**Human confirmation / kill:** Within existing five first-time/plain-language author slots, ask participants what is complete, what still needs repair and what they expect Create Action to do; stop before activation. Confirm if they interpret visible creation as proof of readiness or cannot describe the warning boundary. Kill if all five independently explain the structural incompleteness and can distinguish authoring intent from execution. Functional validation of creation belongs to a separately authorized task.

## Boundaries and carried research

The **existing 75 proposed qualitative session slots remain 75; zero are executed**. The tasks above fit its prepared/first-time/keyboard/low-vision author lanes and require no additional allocation. Low-vision slots should investigate the 63px-flow condition with actual working window sizes and native browser zoom, recording the method; CSS zoom2 does not stand in for that evidence. These panels are qualitative hypotheses, not human success estimates.

AI consent, disclosure burden, legal/tax wording, US guide relevance and preservation of detailed economic decisions remain open previous questions. Pass06 never opens the AI panel, creates a provider response or exercises an economic handoff, so it cannot resolve them. Clear labels, high dark text contrast, zero axe findings and reversible synthetic edits provide bounded positive evidence. None establishes complete WCAG/accessibility, legal/tax sufficiency, trustworthy economic automation, AI re-import success, light-theme quality or release approval.
