---
type: UX Research Panel
pass: 06-release-readiness
lens: accessibility
status: SOURCE_FREE_HYPOTHESES_FOR_VALIDATION
---

# Accessibility panel: pass06

The selected manual editor now has positive programmatic-label and dark-text-contrast evidence, but zero axe violations does not clear keyboard focus or layout access. Delete Node's recorded settled keyboard-focus appearance matches rest, with a transparent outline and no shadow. The narrow viewport leaves only 63px for the graph, while root CSS magnification collapses its measured width to zero. These are observed presentation concerns; native browser-zoom conformance and human exclusion have not been established.

This report uses only pass06 scenario/personas/rollup, the manual lane summary, permitted geometry/focus/history reports and screenshots, plus the public pass05 accessibility panel and human-validation plan. Private paths identify authorized review evidence; no screenshots, full transcripts, identifiers or private brief are republished. No source, trace, credentials, raw network, browser/API/model or environment operations were used. This is agent QA, not human research or WCAG certification.

## Observed scope

`ux-report.json` records eight UI journeys, seven COMPLETED and one manual-editor PARTIAL, plus a separate probe. All three gold tasks passed; coverage remains 12/57. Recorded friction is 6.625 versus 1.875 previously, failing the 10% tolerance. Keep the raw score: this manual job replaces the AI interview/import and is not a like-for-like regression estimate. The manual lane records 9 ordinary nodes, **0 edges**, 0 interview POSTs, three initial failed selection attempts, recovery by visible panning and no API/source workaround. Seven rage-click proxy events include planned repeated palette clicks, not measured human distress.

In `flows/manual-planning-editor/lane-report.md`, Name and Config (JSON) have programmatic names and clicking their visible labels focuses the corresponding fields. `keyboard-focus-path.json` records the path Name → Entry → Terminal → Config → Delete Node; this proves local reachability, not full nonvisual graph operation. Name/config edits and Undo/Redo restored original values. Four selected-node/keyboard/narrow axe scans are clear. The report records Delete text contrast of 11.44:1 at rest/focus and 9.93:1 at settled hover in the actual dark appearance. The pass05 label and text-contrast failures were not reproduced in these sampled manual states; their historical records remain unchanged. Actual light-theme coverage is unverified: emulated color-scheme preferences did not change the root dark appearance.

## Findings and validation questions

Severity is impact × frequency × segment breadth, each 1–3. Ratings below are human-impact hypotheses grounded in one lane, not prevalence measurements. An impact-3 accessibility exclusion receives at least MEDIUM even with a low numerical product.

### A06-1 — Keyboard focus on Delete Node is not visibly distinguished

- **Observed evidence/route:** `/actions`; `flows/manual-planning-editor/delete-focus-comparison.json` has `focusVisible: true` after keyboard focus but identical foreground, background, border, outline, shadow, filter and transform to rest. The outline is transparent 2px and shadow is none. `runs/qa-uxqa-ai-tenant--manual-planning-editor-regression/delete-rest.png` and `delete-keyboard-focus-settled.png` show no obvious new Delete focus indicator. This panel inspected both. `keyboard-focus-path.json` also samples transparent outlines/no shadows on Name/Config, but does not fully compare their other focus styling; do not generalize the complete Delete comparison to those fields.
- **Hypothesis/job:** A sighted keyboard user cannot reliably locate focus before choosing the destructive action. WCAG **2.4.7 Focus Visible** is the supported failure candidate; a true `:focus-visible` state is not a painted visible indicator. Nielsen 1/5; job: know which control will activate while editing safely.
- **Rating/confidence:** impact **3**, frequency **1**, breadth **1** = **3, MEDIUM accessibility floor** for potential keyboard exclusion. High confidence in the recorded comparison and lack of an obvious screenshot indicator; medium confidence in human task exclusion. This is not a HIGH claim or a code-cause diagnosis.
- **Confirm/kill:** In the existing five keyboard/screen-reader author slots, ask sighted keyboard users to locate the currently focused field/control without activating Delete. Confirm if they cannot identify focus or risk activating the wrong action. Kill the exclusion hypothesis if a reliable visible indicator is demonstrated and users track it independently. A technical closure still requires independent settled paint/focus verification; axe0 or focusability cannot close it.

### A06-2 — Narrow editor layout substantially constrains graph access

- **Observed evidence/route:** `/actions`; `narrow-geometry.json` measures a 683×900 viewport, graph x278–341, width **63px**. `narrow683-toolbar-flow.png` shows the palette, tiny central graph strip and inspector side by side, with toolbar text partly constrained. `css-zoom2-geometry.json` separately measures root CSS `zoom: 2` at 1366×900, graph width **0** at x1004, y1132, with lower toolbar controls below the viewport; `css-magnification2-toolbar-flow.png` supports the constrained state. The toolbar remains separated from the flow; separation alone does not preserve usable graph space.
- **Hypothesis/job:** Authors using narrow windows or magnification may be unable to inspect/select graph cards while retaining inspector context. Nielsen 7/9. WCAG **1.4.4 Resize Text** and **1.4.10 Reflow** are validation targets, not established native-zoom failures from these probes. A spatial graph's layout exceptions and surrounding controls need separate assessment.
- **Rating/confidence:** impact **2**, frequency **1**, breadth **1** = **2, LOW provisional impact**; high confidence in the measured constrained geometry, medium in costly access detour, low in complete exclusion. Raise to at least MEDIUM if a constrained segment's essential task is shown unavailable. No successful narrow graph task was recorded.
- **Confirm/kill:** Use the existing five low-vision author slots with participants' normal **native browser zoom**, narrow windows and a 320 CSS-pixel reflow assessment. Ask them to select and inspect first/middle/last cards and move between panels. Confirm if content/control loss prevents the job or imposes a material workaround. Kill for tested states if an accessible arrangement or discoverable ordinary navigation reliably supports completion. CSS-root zoom is not native 200% zoom; neither 683px nor CSS2 proves mobile/reflow conformance.

### A06-3 — Initial graph framing may make keyboard/magnified recovery costly

- **Observed evidence/route:** `/actions`; `desktop-geometry.json` measures flow x502–1024, width522, with first card x158–248 and last x1278–1368 outside that clip after Auto-layout. `nine-nodes-auto-layout.png` shows only the middle part of the disconnected row. The lane report records three failed wrapped first-node attempts, retained abandonment marker, then ordinary pan recovery and verified first/middle/last card-center selections. Middle-card geometry remained stable after selection; toolbar clearance was at least9px.
- **Hypothesis/job:** Constrained authors may not discover how to reach a clipped endpoint. Nielsen 7/9; WCAG 2.1.1 is a keyboard-operation coverage question, not a demonstrated keyboard failure in this pointer task. Job: independently inspect the entire manual graph.
- **Rating/confidence:** **2 / 1 / 1 = 2, LOW**; high confidence in clipping and the initial selection failure, low in constrained-segment exclusion. The eventual pointer recovery prevents claiming no available recovery path.
- **Confirm/kill:** Map first/middle/last card navigation to the existing keyboard/screen-reader and low-vision author slots, five per segment. Confirm if users cannot discover/reach endpoints with their normal controls. Kill if provided pan/zoom/keyboard alternatives yield reliable independent inspection without material detour. Do not convert this pass's later recovery into an initial success or remove its PARTIAL outcome.

## Coverage and human plan

The manual lane did not expand AI, regenerate/import a draft, create edges, save, publish, run, delete or notify. It cannot establish accessible edge creation, connected graph reading, AI reply/status behavior, light appearance, persistence or execution. A focus path through inspector fields is not a complete screen-reader canvas test. Long scope copy, relevant adviser sources and preservation of detailed decisions remain carried questions; this provider-free pass supplies no new evidence resolving them.

`uxqa/human-validation.md` already proposes **75 slots, zero executed**, and expressly adds pass06 focus/pan/zoom tasks within them. Use its five keyboard/screen-reader author slots for A06-1/A06-3 and five low-vision author slots for A06-2/A06-3; keep technology-specific observations distinct. The operator-access slots retain initial/loading/loaded/error-state checks, which this manual lane does not answer. No participants or sessions are added or counted as executed here.

Retain the objective improvements and remaining focus/layout concerns separately. This lens substantiates a MEDIUM focus candidate, not a clean accessibility result or complete release verdict. No HIGH is demonstrated by this bounded evidence; whole-pass triage remains the coordinator's responsibility.
