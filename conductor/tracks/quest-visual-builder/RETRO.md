# Retro: quest-visual-builder

**Shipped 2026-06-20. Author: Claude (Opus). One session, ~7 new files, one new dependency.**

## What we set out to do

The Quest DAG page rendered the engine's full capability through two JSON
`<textarea>`s and a numbered text list. The engine (quest-api +
durable-workflow-engine + economic-primitive-nodes) deserved an authoring
surface that matched it. Goal: a real drag-and-drop workflow builder + first-class
template creation, with **zero backend changes**.

## What shipped

- `@xyflow/react` v12 as the workflow canvas — the de-facto React DAG builder.
- A `quest-builder/` component package: node catalog, custom node, dependency-free
  layered auto-layout, read-only DAG render, interactive builder canvas, node-template
  creator.
- The page rewired to 4 working tabs (My Quests / Builder / Quest Templates /
  Node Templates), with browse **and create** on both template tabs.
- A template-driven palette: authoring a Node Template enriches the builder.

## What went well

1. **Catalog-as-SoT.** Mirroring `QuestNodeType` (the C# enum) into one
   `node-catalog.ts` kept the palette, inspector, and color system in lockstep.
   The `requiresChain` flag carried the economic-primitive-nodes Tier-2 semantic
   straight into the UI for free.
2. **No-backend-change discipline.** Serializing to the existing index-ref
   `{nodes, edges}` contract meant the whole feature is additive frontend work —
   no API, no schema, no migration risk.
3. **Avoided dagre/elkjs.** A ~40-line longest-path layering pass was enough for
   quest-sized graphs and kept the dependency count at +1.
4. **Verification was honest and scoped.** Per the standing no-frontend-typecheck
   rule, `tsc --noEmit` was filtered to the new files only — caught a real
   Map-iteration target error, fixed, re-confirmed clean.

## What was tricky / lessons

1. **TS target vs. `for...of` over Maps.** The project's TS target rejects direct
   Map iteration without `downlevelIteration`. `Map.forEach` sidesteps it cleanly.
   Worth remembering for any future `quest-builder` additions.
2. **MiniMap `nodeColor` wants a CSS color, not a class fragment.** First pass
   returned a Tailwind class stripped of `bg-`; had to add an explicit hex map.
   React Flow's imperative bits don't speak Tailwind.
3. **Memory note said "rebuilt" but the system-reminder paraphrased the index.**
   The MEMORY.md index line had to be appended by reading the real file, not the
   reminder text — a reminder that injected context is a paraphrase, not the file.

## Follow-ups (deliberately deferred)

- **Edit-in-place** for existing quests (builder is create-only today).
- **Schema-driven config forms** — node templates already persist a `configSchema`;
  rendering it as a form (instead of a JSON textarea) is the natural next step and
  would make the builder genuinely no-code.
- **Server-side node positions** — layout is recomputed on every load; persisting
  canvas coordinates would preserve hand-arranged graphs.
- **Streamline page layout** — DONE same session (2026-06-21): quests + star-odk
  pages converted to full-width stacked layout (tab bar + action-button row on top,
  full-width divided list beneath with inline-expanding detail rows via a
  `ChevronRight` disclosure). Replaced the cramped 3-col split (quests) and the
  Table + separate detail Card (star-odk). `tsc` clean.

## Relationship to the fractionalization question

The user asked whether an asset can be represented holonically, notarized with a
mint, then fractionalized via a token launch backed to that asset's state — the
rails ArdaNova would consume through a STAR-ODK. This builder is the *authoring
front door* for exactly that kind of multi-step flow (Holon → Mint → Swap/Grant),
but the **fractionalization primitive itself is a backend gap** (no fractional/
fungible-share mint or asset-backed token node exists yet). See the separate
investigation; if pursued it becomes its own track and a new `QuestNodeType` that
this builder's catalog would then surface automatically.
