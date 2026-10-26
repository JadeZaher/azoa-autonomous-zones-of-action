---
type: UX Research Panel
title: Pass06 content-design panel
description: Artifact-only content interpretation of the manual editor regression journey.
---

# Content-design lens

The manual editor now supplies usable programmatic names for the checked Name and Config fields, but understanding the graph's state still requires reading technical warnings and recovering context after navigation/history changes. Pass06 remains **PARTIAL**; later successful panning and editing do not erase its three initial failed attempts.

One analyst wrote this report and the separate pass06 trust-behavioural report in two lens passes. They are distinct interpretations of the same evidence, **not two independent analysts or corroborating human sessions**. This panel read only permitted UX artifacts: pass06 scenario/personas/report, manual lane report, rendered visible text, geometry/focus/history/contrast/appearance reports and selected screenshots viewed with `view_image`, plus the human-validation plan. It inspected no application source, source-triage material, secrets, traces, browser/API/model responses or live environment. Private evidence is referenced by path; no screenshots, raw transcript or identifiers are published.

## Observations

- The lane manually added nine ordinary Emit nodes and no connections: **9 nodes / 0 edges**, zero interview requests and no AI panel expansion/import replay. These are not the pass05 AI-generated planning nodes. No save, publish, run, delete, notification or economic action occurred.
- At 1366×900, toolbar controls end at y302 and flow begins at y311. Toolbar separation is supported. The disconnected-node auto-layout nevertheless places first/last cards outside the 522px flow clip. Three wrapped first-node selections fail; visible canvas panning later enables normal first/middle/last selection. The raw outcome and abandonment marker remain.
- Label-click focuses the corresponding Name input and Config textarea. Valid synthetic edits and Undo/Redo restore actual values, with 9/0 retained. History clears selection; the inspector reverts to generic selection instructions, requiring reselection to inspect the restored values.
- Visible warnings correctly identify disconnected/orphan nodes, absent Terminal and nodes unreachable from Entry. Their technical terms are not evidence that a novice understands the remedy. The Create Action control is visible but was not activated; its behavior cannot be inferred.
- Four manual-lane axe scans report zero violations. Delete text contrast passes in checked dark appearance; rest/focus is 11.44:1 and settled hover 9.93:1. Light appearance was not exercised: changing browser color preference left the actual interface dark. Delete's settled focus/rest styles remain visually indistinguishable in the saved comparison despite keyboard focus. Focusability and clear text do not prove visible focus.
- A 683×900 window leaves the graph only **63px wide**; toolbar and flow remain separate. Root CSS zoom2 at 1366×900 leaves zero measured flow width. Neither check is native browser zoom or complete mobile/reflow testing.
- The aggregate is seven completed UI journeys plus one partial manual journey and a separate probe; three gold predicates pass, coverage remains 12/57, zero aggregate axe violations, friction **6.625** versus 1.875 in pass05 and trend gate false. The manual job differs from the former AI interview. Its 41 friction includes three failed attempts, two dead clicks and seven rage-click proxies; intentional repeated palette construction is not demonstrated human frustration. No score is repaired here.

## Content hypotheses

Scores are impact × observed frequency × segment breadth (1–3 each). Single manual-lane evidence does not establish broad frequency. Each finding remains a research question; code causes are outside this panel.

### CD06-1 — Diagnostic graph terms may not tell a novice what to do first

**Hypothesis:** A novice seeing overlapping orphan/unreachable warnings may treat them as separate failures or struggle to connect “Entry,” “Terminal,” and “incoming edge” with a concrete repair sequence.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/after-wrapped-selection-visible.txt`, `undo-cleared-selection-visible.txt`; `runs/qa-uxqa-ai-tenant--manual-planning-editor-regression/undo-cleared-selection.png`. The 9/0 construction genuinely warrants connectivity warnings. The Terminal warning already offers an explicit remedy, which counters a blanket “no help” interpretation. No connection repair was attempted.

**Nielsen / JTBD:** 2 match with the real world; 9 error diagnosis/recovery; 10 documentation. Job: understand why my manually constructed graph is incomplete and identify a safe next edit.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in the rendered wording; low-to-moderate confidence in novice difficulty.

**Human confirmation / kill:** Within the existing five first-time/plain-language author slots, present the disconnected fictional graph and ask for an own-words diagnosis and first repair plan, stopping before execution. Confirm if participants cannot connect warnings to the missing edges/terminal or repeat unrelated edits. Kill/narrow if all five independently describe the missing structure and an accurate first edit. Do not claim warning correctness for every graph from this one state.

### CD06-2 — Generic empty-inspector copy can obscure successful Undo

**Hypothesis:** After Undo/Redo clears selection, “Select a node” may make an author unsure whether the edit was reversed or the editor lost context. They may repeat Undo or re-enter data before checking the intended node.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/undo-redo-visible.json`, `undo-cleared-selection-visible.txt`; manual run `undo-cleared-selection.png` and `undo-redo-restored.png`. Values were correctly restored and counts stayed 9/0. The lane's extra read wait and reselection were context-recovery costs, not data loss.

**Nielsen / JTBD:** 1 visible status; 3 control/freedom; 6 recognition. Job: reverse a small edit and immediately verify what changed.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in selection clearing and correct value history; moderate confidence in uncertainty for people.

**Human confirmation / kill:** Within five prepared author sessions, have participants reversibly edit one fictional name/config, Undo, explain the result, then verify by reselection. Confirm if they believe content vanished or perform extra history actions because the empty inspector hides feedback. Kill if all five accurately infer and verify the restored state unaided without extra edits. Do not activate deletion/save/run.

### CD06-3 — Auto-layout may imply a reviewable arrangement while hiding the first and final nodes

**Hypothesis:** Users may expect Auto-layout to leave all content discoverable, then misread clipped/small cards as missing content or fail to find a named node. The generic inspector instruction does not explain the current framing limitation.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/desktop-geometry.json` and `lane-report.md`; manual run `nine-nodes-auto-layout.png`, `first-node-visible-pan.png`, `last-card-selection-verified.png`. First/last cards are outside the clip; three initial attempts fail; ordinary panning subsequently works. The visible minimap and zoom controls offer recovery and must be acknowledged.

**Nielsen / JTBD:** 1 visibility; 6 recognition; 7 efficiency. Job: find and inspect every manually added checkpoint without assuming content was removed.

**Rating / confidence:** 2 × 1 × 1 = **2, LOW**. High confidence in the observed detour; moderate confidence in the predicted mistaken inference. Later recovery does not turn the frozen lane into completed.

**Human confirmation / kill:** In existing five prepared author sessions, ask for first/middle/final selection from initial auto-layout using normal pan/zoom. Confirm if authors mistake clipping for loss, cannot identify recovery controls or incur repeated failed selection. Kill/narrow if all five find every card unaided, preserve context and explain that the graph extends beyond the view. Planned rapid palette clicks alone cannot confirm frustration.

### CD06-4 — Named inspector fields do not ensure readable cross-panel context at narrow sizes

**Hypothesis:** When palette and inspector consume nearly all horizontal space, an author may read field labels correctly yet be unable to relate the selected node, warnings and configuration. Narrow layout can therefore undermine the meaning of otherwise accurate labels.

**Evidence / route:** `/actions`; `flows/manual-planning-editor/narrow-geometry.json`, `css-zoom2-geometry.json`; manual run `narrow683-toolbar-flow.png` and `css-magnification2-toolbar-flow.png`. Flow width is 63px at the actual 683px window and zero in the separate root-CSS-zoom2 probe. The latter is not native 200% zoom evidence.

**Nielsen / JTBD:** 1 visibility; 6 recognition. Job: relate a node to its editor and safely review the whole graph at my usable reading size.

**Rating / confidence:** potential impact 3 × 1 × 1 = 3; **MEDIUM under the accessibility impact-3 floor** for potential exclusion. High confidence in the bounded geometry; moderate confidence in narrow-window task obstruction; native-zoom consequences remain unknown. This is not a WCAG conformance determination.

**Human confirmation / kill:** Use the existing five low-vision author slots with their normal native browser zoom and narrow windows, recording actual methods. Ask for a first/middle/final review and field identification. Confirm if content cannot be read/reached or the node/inspector relationship is lost without outside help. Kill/narrow the human-outcome hypothesis if all five complete independently at their working size; do not dismiss technical zero/63px evidence or call CSS zoom equivalent to native zoom.

## Carried limits

The existing plan proposes **75 qualitative session slots; zero have occurred**. The checks above fit its author/accessibility tasks and add no sessions. Long AI scope, US reading-guide relevance, disclosure, and preservation of detailed decisions remain open prior questions: pass06 kept AI closed and supplies no fresh evidence for their resolution. Programmatic naming, selected-state axe and checked dark contrast provide focused regression evidence only. No legal, tax, WCAG, AI re-import, economic execution or release certification is made.
