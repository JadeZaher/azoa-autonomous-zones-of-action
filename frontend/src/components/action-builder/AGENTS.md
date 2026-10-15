# components/action-builder — design notes

The Action DAG authoring surface: a React Flow canvas with a node palette on the
left and an inspector on the right, plus the read-only run views that reuse the
same node card. `action-canvas.tsx` is a composition root only — every piece of
state lives in `hooks/`, every piece of markup in `panels/`, and every rule that
mirrors a backend validator in a plain `.ts` module at this level.

## §module-map

This table records **what each file is for**, and deliberately records nothing
about who imports what. Three successive revisions of this document shipped a
wrong import claim (`node-output-schema.ts` credited to `dag-flow.tsx` and
`ecosystem-tree`, which never imported it; `presets.ts` likewise; a whole live
file, `node-template-creator.tsx`, missing altogether) because an edge list is
exactly the kind of fact that rots silently the moment a file moves. Import
edges are one `grep -rn "from '.*<file>'" src/` away and are always accurate;
purpose is not discoverable that cheaply, so purpose is what lives here.

| File | Role |
| --- | --- |
| `action-canvas.tsx` | Composition root. Owns the public surface: `ActionCanvas`, `BuiltGraph`, `NodeTemplateMeta` (the last two re-exported). |
| `action-node.tsx` | The node card itself (`ActionNode`) and `ActionNodeData`, shared by the builder and the read-only run views. |
| `hooks/use-action-selection.ts` | Which node / edge the inspector edits, plus a read-only mirror of React Flow's own multi-selection. |
| `hooks/use-action-graph-state.ts` | Nodes + edges + every mutation (add, connect, patch, delete, auto-layout, clear, preset load, paste) and its undo/redo history. |
| `hooks/use-graph-history.ts` | The undo/redo stacks themselves — snapshot/restore, decoupled from any one mutation. |
| `hooks/use-subgraph-clipboard.ts` | Copy the current multi-selection into an in-memory clipboard; paste lives in `use-action-graph-state.ts` (see §clipboard). |
| `hooks/use-canvas-keyboard-shortcuts.ts` | Ctrl/Cmd+Z / Shift+Z / C / V, ignored while a text field has focus. |
| `hooks/use-node-palette.ts` | Palette entries, search filter (name/type/description/category), category grouping, and keyboard-nav active row. |
| `hooks/use-palette-drag-and-drop.ts` | The `application/action-node` drag protocol between palette and canvas. |
| `hooks/use-dag-validation.ts` | Blocking conditions + warning list + the per-node `nodeWarningsById` decoration. |
| `hooks/use-action-submission.ts` | Serialize-and-submit, and why the submit button is blocked. |
| `hooks/use-node-focus.ts` | Click a warning → select and center that node. |
| `panels/*` | Presentational only. No state beyond local view concerns. |
| `edge-model.ts` | `EdgeData` + `edgeStyle` (edge type → stroke/dash/animation). |
| `node-palette.ts` | Catalog ∪ API node templates → `PaletteEntry[]`. |
| `node-catalog.ts` | The static `NodeTypeMeta` table mirroring the backend `ActionNodeType` enum (50 members, see §catalog-status), plus category colors/order and the `status` preview flag. |
| `node-output-schema.ts` | Per-node-type output shape mirror (same 50 members), used to check `$from` bindings and to annotate run output. |
| `presets.ts` | Starter flows offered by the toolbar's Presets menu. |
| `layout.ts` | Execution-layered auto-layout (`layoutGraph`), shared with the read-only renders. |
| `dag-analysis.ts` | `$from` binding-site collection, dominator dataflow, config-JSON validity. |
| `dag-warnings.ts` | The authoring pre-check itself; pure, takes nodes/edges, returns `DagWarning[]`. |
| `graph-serialization.ts` | Canvas graph → `BuiltGraph` (the POST body). |
| `dag-flow.tsx` | Read-only React Flow render of a fetched action's DAG (the "My Actions" detail panel). Not part of the builder. |
| `run-panel.tsx` | Durable-run controls for an existing action. Not part of the builder. |
| `node-template-creator.tsx` | Standalone "create a Node Template" form, mounted from the actions page alongside the builder. Not part of the builder. |

## §seams

The split follows the four queued feature changes: each of graph mutation,
validation, submission, and inspector rendering can be edited without opening the
others. Rules of thumb when extending:

- A hook may depend on another hook's **stable functions**, never on its returned
  object (object identity changes every render and would break memoization).
  `useActionGraphState` therefore takes `setSelectedNodeId` / `setSelectedEdgeId`,
  not the whole selection controller.
- Panels take data + callbacks. `ActionFlowCanvas` and `InspectorPanel` accept
  `children` for their overlay/footer so the root never re-creates their wrapper
  markup.
- New backend-mirroring rules belong in `dag-warnings.ts` (or a helper in
  `dag-analysis.ts`), never inside a hook — they must stay unit-testable without React.

## §selection

Selection is deliberately two independent ids, not a tagged union, because the
canvas semantics differ per mutation: clicking a node or edge clears the other,
but **adding** a node selects it without clearing an edge selection, and deleting
clears only its own kind. `useActionSelection` exposes both the composed helpers
(`selectNode`, `selectEdge`, `clearSelection`) and the raw setters so each call
site keeps its exact original behavior.

**React Flow owns node selection. We only mirror it.** React Flow toggles each
node's own `.selected` field (plain click = single-select, Ctrl/Cmd+click =
additive), delivers those as `select` changes through `onNodesChange` — which
`useActionGraphState` applies to the nodes array like any other change — and
reports the resulting set through the official `onSelectionChange` event.
`useActionSelection.handleSelectionChange` observes that event so non-canvas
consumers (the copy/paste toolbar) can ask "what is selected?"; the resulting
`selectedNodeIds` is a **read-only mirror** — it must never be fed back into
`<ReactFlow>`. (`selectEdge`/`clearSelection` do clear the mirror eagerly, which
is safe: React Flow unselects the nodes on those same events, so the mirror is
only arriving at the value the next `onSelectionChange` reports anyway.)

### §selection — the closed loop, and the render loop

Two failure modes live here. Both shipped once; both now have regression tests
(`__tests__/action-canvas-selection.test.tsx`, `__tests__/action-canvas-render-loop.test.tsx`).

- **Never write the mirror back onto the nodes array.** A previous
  `useSelectionFlaggedNodes` helper rewrote every node's `.selected` from
  `selectedNodeIds` on the way into `<ReactFlow>`. Since `adoptUserNodes` spreads
  the user node over the internal one, the prop always won, so the store could
  only ever report back the value we had just fed it: a closed loop seeded at
  `[]`. Multi-select was permanently empty, Copy and Paste were permanently
  disabled, and `action-node.tsx`'s `ring-2 ring-primary` never rendered even for
  single-select. The helper is deleted. If a controlled projection is ever
  genuinely wanted it must MERGE with the incoming `.selected`, not clobber it.
- **`onSelectionChange` must be passed by reference.** React Flow's
  `SelectionListenerInner` lists `onSelectionChange` in its effect deps, and
  `<ReactFlow>` is `fixedForwardRef`, **not** `memo` — it re-renders whenever the
  canvas does. An inline `({ nodes }) => …` arrow is therefore a fresh dependency
  every render, so the effect re-fires, calls `setState`, and renders again,
  forever (measured: 300+ commits and "Maximum update depth exceeded", with and
  without StrictMode). `handleSelectionChange` takes React Flow's own `{ nodes }`
  params object precisely so the JSX can hand it over unwrapped, and it
  additionally bails when the incoming id list is unchanged, since `nodes.map()`
  is always a fresh array that would never be `Object.is`-equal to the previous
  state. The bail-out alone stops the runaway; the stable identity is what makes
  a selection cost one render instead of two.

## §graph-state

`useActionGraphState` owns nodes/edges plus every mutation, and wraps each one
(`addNode`, `connectNodes`, `patchNode`, `patchEdge`, `deleteNode`, `deleteEdge`,
`autoLayout`, `clearGraph`, `loadPreset`, `pasteSubgraph`) with `useGraphHistory`
so every authoring action is undoable. `onNodesChange`/`onEdgesChange` — React
Flow's own change handlers, which the native Backspace/Delete key path calls
directly and which therefore bypass `deleteNode`/`deleteEdge` entirely — are
also wrapped, but ONLY snapshot history on a `'remove'`-type change; drag/resize/
select changes fire on every pointer-move frame and must never touch history.

`uniquifyLabel` (top-level pure helper) is shared by `addNode` and
`pasteSubgraph` — both need the exact same "auto-suffix on collision" rule
against a live label set, and duplicating that loop would be the kind of drift
that eventually produces two different definitions of "unique."

### §history — the StrictMode trap

`next.config.js` sets `reactStrictMode: true`, and React 18 double-invokes the
FUNCTION passed to `setState`/`setNodes`/`setEdges` in dev to surface impure
updaters — the SAME mechanism behind the `addNode` stale-closure fix in
§known-quirks. `useGraphHistory.pushHistory` must never be called from inside
one of those functional updaters, or a single user action records two (or more)
history entries and undo silently does nothing on the first press.

The fix: every mutator calls `pushHistory()` as a **plain statement in its own
body**, before touching `setNodes`/`setEdges` — a normal function call,
invoked exactly once per real user action, never doubled. `pushHistory` itself
reads the pre-mutation snapshot from `nodesRef`/`edgesRef`, refs synced in a
plain (no-deps) `useEffect` — never written during render, which the
`react-hooks/refs` lint rule forbids — so by the time a real user event fires
they already hold the latest committed state. `pushHistory` keeps a zero
dependency array regardless, which is also why its identity never changes and
doesn't destabilize any mutator's `useCallback` deps (destructure `pushHistory`
out of the hook's return rather than depending on `history.pushHistory` —
the member-expression form doesn't read as stable to `exhaustive-deps`).
`undo`/`redo` read `past`/`future` directly from
render-time state (not a functional updater) since they only ever run from a
real keyboard/click event, and pass `setNodes`/`setEdges` a **direct array**,
not an updater function — so there's nothing for StrictMode to double-invoke
on the undo/redo path at all.

### §history — one user action, one entry

The rule above ("plain statement in the action body") is necessary but was not
sufficient, and the gap cost a real bug. React Flow's `deleteElements` — the
Backspace/Delete path — fires `triggerEdgeChanges(…)` and `triggerNodeChanges(…)`
as back-to-back **synchronous statements**. Deleting one connected node therefore
runs BOTH of our change wrappers, each sees a `'remove'`, and each calls
`pushHistory()` — twice, in one tick, from two different plain statements that
are individually correct. Worse, no commit can happen between two synchronous
statements, so the effect-synced `nodesRef`/`edgesRef` still held the identical
pre-delete arrays and both entries were byte-for-byte the same. Symptom: delete a
connected node, Ctrl+Z restores it, **the second Ctrl+Z does nothing**, the third
undoes the action before it.

`pushHistory` now drops a snapshot that is **reference-identical** to the newest
entry already on the stack. Reference identity is the exact test, not a
heuristic: every mutator replaces the arrays it touches, so identical references
mean the graph has not changed since that snapshot and there is nothing extra to
undo *to*. Deleting an edge on its own is unaffected — only `onEdgesChange` fires
there, so there is no preceding identical entry to collapse into — which is why
the edge-stream wrapper must stay. Both cases are covered in
`hooks/__tests__/use-action-graph-state.test.tsx`.

## §clipboard

Copy (`useSubgraphClipboard.copySelection`) is a pure read: snapshot the
selected nodes, plus only the edges whose **both** endpoints are in the
selection (an edge pointing outside the selection would dangle in a detached
paste). Paste (`useActionGraphState.pasteSubgraph`) does the heavier lifting —
fresh ids, remapped internal edges, a position offset so the copy doesn't sit
exactly on top of the original — and lives in `use-action-graph-state.ts` rather
than the clipboard hook because label uniqueness has to be checked against the
**whole current graph**, which only that hook can see.

The offset is `48 × (number of times THIS clipboard has been pasted)`, tracked in
a `pasteRun` ref keyed on clipboard identity. A flat `+48` was wrong: the
clipboard is a frozen snapshot that never re-bases, so Ctrl+V twice put both
copies on exactly the same coordinates and the second looked like it had done
nothing. Copying again produces a new clipboard object, which restarts the run at
one. The counter is stepped in the action body, never inside the `setNodes`
updater, because StrictMode double-invokes updaters and would double-count it.
Labels were always fine (`Foo` → `Foo 2` → `Foo 3`) since `uniquifyLabel` reads
the live node array rather than the clipboard.

Pasted nodes are deliberately stripped of `isEntry`/`isTerminal`: a paste is a
detached subgraph with no wiring into the rest of the canvas yet, and carrying
over Entry would mint a second Entry-flagged node. `computeDagWarnings` doesn't
explicitly flag ">1 Entry" today, but the execution model only ever expects
one true starting node — re-mark deliberately via the inspector once the copy
is wired in. The pasted nodes ARE the new `selectedNodeIds` (each gets
`selected: true`, the rest of the graph deselected), so the user can
immediately drag the fresh copy into place or hit Ctrl+C again to re-copy it.

## §dag-precheck

`computeDagWarnings` mirrors the backend `ActionDagValidator` +
`ActionDagExecutabilityValidator` so authors see structural problems before eating
a 400. It is **not authoritative** — the server re-validates — and it is tuned to
never false-positive: when a shape is open, a name is ambiguous, or a path is
malformed, it skips rather than warns.

Invariants worth preserving:

- Control fan-out (>1 outgoing Control edge) and dominators key on
  `edgeType === 'Control'` **exactly**. `Conditional` is a branch and
  contributes no guaranteed ancestry. `OnFailure` has its own fan-out rule
  (>1 outgoing OnFailure edge — the "exactly-one-arm property, V2",
  `ActionDagValidator.cs:204-226`), tracked separately from Control fan-out, and
  also contributes no guaranteed ancestry. Upstream-scope (`directPredNames`)
  walks *every* incoming edge regardless of type — that asymmetry mirrors
  `BuildUpstreamScope` vs `ComputeDominators` and is intentional.
- `computeDominators`'s entry set is nodes with **no incoming edge of any
  type**, not "zero Control-preds" — a node reached only via a
  Conditional/OnFailure arm has zero Control-preds too, and must not be
  treated as a dataflow root (it would falsely dominate its own
  Control-successors). Dominator sets are also stripped to nodes
  Control-reachable from a true entry after the fixpoint. Both mirror explicit
  guards in the backend `ComputeDominators` (`ActionDagExecutabilityValidator.cs`).
- `error: true` means the server will reject **right now**; anything else is
  advisory. Both fan-out rules are advisory here because the backend only
  promotes them to hard errors under `fanOutAsError` (publish time,
  `ActionManager.PublishAsync`) — creation and the plain "Validate DAG" action
  accept them as warnings. The submit button itself is blocked only by invalid
  config JSON and by Conditional edges with no condition text.
- Iterate `Map`/`Set` with `forEach` or `Array.from` — `tsconfig` targets es5
  without `downlevelIteration`, so direct `for..of` over an iterator will not compile.

Findings are surfaced in two places that stay in sync through the SAME
`nodeWarningsById` map: the aggregate footer list (`DagWarningList`, click a
finding → `onFocusNode` selects + centers that node) and a badge on the
offending node itself (`action-node.tsx`'s `NodeWarningIndicator`, click → the
same finding text; see §warning-badge). The node ring/badge color preserves the
error-vs-advisory split from the bullet above (red for `error: true`, amber
otherwise) rather than flattening it to one color — an advisory fan-out
finding must not read as urgently as a hard 400-on-submit. The reverse path
(select a node → its findings are called out in the footer list) is driven by
`highlightedNodeId` threaded from `selection.selectedNodeId` down to
`DagWarningList`, not a second copy of the "which node is selected" state.

### §warning-badge

`NodeWarningIndicator` is **click-only**, and that is a decision, not an
oversight. It used to open on `onMouseEnter` and close on `onMouseLeave`, which
cannot work: `PopoverContent` is rendered through a **portal** and positioned
with a `sideOffset` gap, so the pointer necessarily leaves the trigger before it
reaches the popup — the popup unmounted mid-journey and its text could never be
hovered, scrolled or selected. The same handlers also broke click outright:
hovering already left `open === true`, so the trigger's own toggle could only
ever *close* it, and click could never open anything. The `title` attribute
alongside it rendered a second, native tooltip on top of the first.

Two repairs were possible — a hover-intent bridge with a grace timer covering the
gap, or click-only. Click-only wins here: no timers, no coupling to Base UI's
open-reason codes, it works on touch and via the keyboard, and the popup stays
put so its text is actually readable. Discoverability rests on the badge glyph
(red `✕` / amber `⚠`), `cursor-pointer`, and an `aria-label` naming the finding
count. If hover-to-peek is ever wanted back, it needs pointer handlers on the
**popup as well as the trigger** plus a close delay, and clicking a
hover-opened popup must pin rather than close it.

## §palette

Palette rows are the static `NODE_CATALOG` **plus** one row per API node
template; templates are appended, not merged, so the same node type can appear
both as a built-in and as a template (the drop handler prefers an exact
`type` + `templateId` match, then falls back to type alone). New node labels are
auto-uniquified (`"Mint NFT 2"`) because node names are the `$from` binding key
and the server rejects duplicates at publish.

### §palette-search

The search input filters on name, node type, description, AND category
(`use-node-palette.ts`), so "economic" surfaces the whole Economic group, not
only entries whose own label happens to contain the word. `flatEntries`
re-derives the SAME visual order the panel renders in (`CATEGORY_ORDER`, then
within-category insertion order) purely so ArrowUp/ArrowDown always lands on
the row that's actually adjacent on screen — keeping two independently-ordered
lists (one for rendering, one for keyboard nav) is how those silently drift
apart the next time a category is reordered. `activeEntry` is a reference into
`flatEntries`/`groupedByCategory`'s shared `PaletteEntry` objects (never
cloned), so the panel highlights it with `===`, not an id comparison. Enter
adds `activeEntry`; Escape clears the filter (not blur) so the next keystroke
starts a fresh search rather than fighting focus.

The exported `setFilter` resets `activeIndex` to `0` in the SAME call, not in a
`useEffect` keyed on `filter` — `react-hooks/set-state-in-effect` flags a
reactive effect that exists purely to call `setState`, and every path that
changes `filter` already goes through this one setter, so there's nothing an
effect would catch that the setter itself doesn't already know about.

## §catalog-status

`NODE_CATALOG` and `NODE_OUTPUT_SHAPE` each mirror **all 50** members of the
backend `ActionNodeType` enum (`Models/Actions/ActionEnums.cs`), one entry per
member, no more, no less. The catalog header has promised that since the file
was born, and it was still seven members short after the Phase 5 rails
(credential D19, governance D20, credit D21) landed server-side — nothing on the
client failed, because `getOutputShapeMirror` treats an unknown type as open and
the palette simply never offered the node. `__tests__/node-catalog-mirror.test.ts`
now carries the enum transcribed verbatim and fails the moment either mirror is
missing a member or carries one the backend doesn't know; adding a backend node
type is not finished until that list, the catalog and the shape map move together.

Two categories were added for the rails: `Governance` (proposal / vote / tally,
pure-DB) and `Lending` (loan ledger, pure-DB). `CredentialRevoke` sits under
`User` beside `UserNFTGetComposite` — a credential is a holder-bound identity
asset, not an economic primitive — and is the only Tier-2 (`requiresChain`) node
outside `Economic`, because revocation publishes an Algorand clawback. Every
money field in the Lending configs is a base-unit decimal **string**; a JS number
silently loses precision past 2^53 and the backend `RailAmount` rejects it.

`NodeTypeMeta.status` (`'stable'` | `'preview'`, default stable) exists because
mirroring the enum and endorsing a node are different promises. Two primitives
are wired but **not enabled**: governance has a live Sybil hole (the tally counts
raw holons; fix tracked as `governance-vote-of-record_20260906`) and credential
revocation is inert until `credential-third-party-issuance_20260906`. Hiding
those four would break the mirror, so they stay in the palette with
`status: 'preview'`, which the palette renders as a dashed, muted row with a
`preview` badge and a tooltip that opens with `PREVIEW_NOT_ENABLED_LABEL`, and
the inspector renders as an amber `role="alert"` banner above the name field.
`node-palette.ts` copies `status` onto template-backed rows so an API template
for a preview type cannot launder it into a buildable one. Presets never
reference a preview type (the drift-guard test asserts it); the one Phase 5
preset is `loan-tracker`, whose `LoanRepaymentRecord` and `LoanBalance` nodes
bind `loanHolonId` from their direct predecessor's `loanId` — all three loan
nodes return the same flat camelCase `LoanBalanceProjection`, so the chain works
with `upstream.` rather than `run.` bindings. Node names are labels, so those
labels are single tokens (`OpenLoan`, `RecordRepayment`) to keep the `$from`
path readable.

Output-shape casing is not uniform across the rails and must not be "tidied":
`CredentialRevoke` serializes `CredentialRevokeResult` directly (PascalCase),
while the governance and loan handlers emit camelCase dictionaries. The presence
check is case-insensitive, so a wrong case would pass here and fail nowhere until
a run — the mirror copies the backend exactly precisely so a reader can diff it
against `Services/Actions/ActionNodeOutputSchema.cs` by eye.

## §serialization

`BuiltGraph.edges[].sourceNodeId` / `targetNodeId` are **array indices into
`BuiltGraph.nodes`**, not Guids — the backend `ActionEdgeCreateModel` binds by
those exact names. Renaming them silently binds every edge 0→0.

## §testing

`npm test` (watch: `npm run test:watch`) runs Vitest over `src/**/*.test.{ts,tsx}`
in jsdom. Config: `frontend/vitest.config.ts`; globals and stubs:
`frontend/src/test/setup.ts`; canvas helpers: `frontend/src/test/action-canvas-harness.tsx`.

This suite exists because `tsc --noEmit`, `eslint` and `next build` were all green
while the builder could not mount at all. Type checks cannot see a render loop, a
closed state loop, or a duplicated history entry. **A change to selection,
history, or clipboard behaviour is not verified until it has been executed.**

What the harness has to paper over, and why:

- jsdom has no layout engine and no `ResizeObserver`, `DOMMatrixReadOnly` or
  `Element.getAnimations`. Without those stubs `<ReactFlow>` cannot mount and Base
  UI's `ScrollArea` throws from a timer. `getBoundingClientRect` / `offsetWidth` /
  `offsetHeight` are stubbed to a non-zero box so React Flow will lay out at all.
- Nodes are never measured, so React Flow leaves each card `pointer-events: none`
  and renders no edges. Consequences: drive clicks with `fireEvent`, not
  `user-event` (which correctly refuses to click through `pointer-events: none`),
  and read graph size from the inspector footer's `N nodes · M edges` readout
  (`graphCounts()`) rather than by counting DOM elements.
- `renderActionCanvas` counts commits through a React `Profiler` and **throws past
  200**. A render loop does not fail a test on its own; it pins a CPU and hangs
  the worker until it is killed, which is precisely how the loop above survived
  review.
- Selection is driven the real way: `fireEvent.click` on a node card reaches
  React Flow's own `handleNodeClick` → `addSelectedNodes` → `onNodesChange`.
  For multi-select, exactly ONE modifier key may be held — React Flow's
  `isMatchingKey` compares the pressed-key set by SIZE, so Meta+Control matches
  neither.
- `deleteElements` is async (it awaits `onBeforeDelete`), so `pressDeleteKey()`
  is async and flushes inside `act`. Toolbar buttons go through
  `clickToolbarButton`, never `element.click()`, which is not act-wrapped.
- Base UI keeps a closing popup mounted for its exit animation, so assert on
  `[data-slot="popover-content"][data-open]`, not mere presence.

`__tests__/node-catalog-mirror.test.ts` is the one pure-data test: no DOM, no
harness. It pins the 50-member `ActionNodeType` transcription against both
mirrors in both directions (missing AND extra), the preview set, the
chain-capability split, string-typed money, category order/color coverage, and
preset loadability. When the backend enum changes, this is the test that goes
red first — update the transcription in the same commit as the mirrors.

## §known-quirks

Behavior preserved as-is through the decomposition; fix deliberately, not by accident.

- FIXED: `addNode` used to depend on `[nodes.length, ...]` while reading `nodes`
  in its body, so a rename (identity change without a length change) could mint
  a duplicate label from a stale closure. It now builds the new node inside the
  `setNodes` functional updater, so it reads the live array with no `nodes`
  dependency at all.
- REMOVED: `readOnly` was threaded through every panel (palette, canvas,
  inspector, footer) but never actually passed `true` by either `ActionCanvas`
  call site (`actions/page.tsx`'s `ActionBuilder` and `ActionTemplateCreator`,
  both create-new-action flows) — the whole branch was dead, untested code.
  There is also no "edit an existing action" flow anywhere in the app to wire
  it to: the "My Actions" tab renders an existing action's DAG with the separate
  read-only `DagFlow` component, not `ActionCanvas`, and offers no node/edge
  editing at all (only Publish/Unpublish/Validate/Delete on the Actions tab).
  Deleted the prop end-to-end rather than leave it unreachable; `onAddNode`
  and `EdgeInspector`'s `onPatch`/`onDelete` are now required props instead of
  optional ones threaded through a conditional, so a future caller that forgets
  to wire a callback gets a compile error, not a silently dead control.
- FIXED: `clearGraph` **and** `loadPreset` now reset `selectedEdgeId` alongside
  `selectedNodeId` — both replace the whole node/edge arrays, so both can leave
  a `selectedEdgeId` dangling. Harmless either way (`selectedEdge` is derived
  via `edges.find`, so a stale id already resolves to `null`), but keeping them
  consistent means the next reader doesn't have to re-derive that it's safe.
- `ActionFlowCanvas`'s `wrapperRef` is attached but never read.
- `edgeStyle` is typed to return `label` but never sets one; edge labels are only
  produced by the read-only `dag-flow.tsx`. Because the type says otherwise,
  `toFlowEdge` spreads `...edgeStyle(…)` **above** its own `label` key, so a style
  table that starts returning one can never blank the condition text. Covered by
  `__tests__/dag-flow-edges.test.ts`, which mocks `edge-model` to return a label.
- FIXED: `CanvasToolbar` rendered `DropdownMenuLabel` as a direct child of
  `DropdownMenuContent`. Base UI's `Menu.GroupLabel` reads `MenuGroupRootContext`
  and **throws when it is missing — in production builds too** — so opening the
  Presets menu took the whole React tree down. It is wrapped in
  `DropdownMenuGroup` now. `src/components/layout/header.tsx` still has the same
  shape and is outside this directory; it needs the same fix.
- RENAMED: `ActionNodeData.hasWarning` (boolean) → `warnings` (`DagWarning[]`) so
  the node card can render severity and message text, not just a flag. Nothing
  outside `action-builder/` referenced the old field.
- Toolbar icon buttons (`CanvasToolbar`) intentionally use the plain `Button`
  component with a native `title` attribute rather than wrapping it in the
  `Tooltip` primitive: `TooltipTrigger`/`PopoverTrigger` render their OWN
  `<button>` by default, and nesting our `Button` (also a `<button>`) inside one
  produces invalid, hydration-unsafe nested-button HTML. The node warning badge
  (`NodeWarningIndicator`) avoids this by putting a plain glyph — never another
  `Button` — inside `PopoverTrigger`.
- FIXED, all four written up in full above: the `onSelectionChange` render loop
  and the clobbered-`.selected` closed loop (§selection), the duplicated history
  entry on delete (§history — one user action, one entry), and the
  never-re-based paste offset (§clipboard). Every one of them was invisible to
  `tsc`, `eslint` and `next build`; see §testing before changing any of them.
