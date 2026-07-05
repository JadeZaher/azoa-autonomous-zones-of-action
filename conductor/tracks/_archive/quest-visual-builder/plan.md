# Plan: quest-visual-builder

Shipped in one session 2026-06-20. Recorded here as the as-built task log.

## Phase A — Discovery
- [x] Explored existing `quests/page.tsx`: single-file, 4 tabs, JSON-textarea
      authoring, text-list "DAG view", read-only template tabs.
- [x] Confirmed no graph lib installed; React 18 / Next 14 / Tailwind + shadcn.
- [x] Read SDK quest types (`sdk/azoa-wallet/src/api/client.ts`) + backend
      `QuestNodeType` enum (`Models/Quest/QuestEnums.cs`) as the catalog SoT.
- [x] Confirmed `select`/`dialog`/`checkbox`/`scroll-area` shadcn primitives exist.

## Phase B — Dependency + catalog
- [x] `npm install @xyflow/react` (v12.11.0).
- [x] `node-catalog.ts` — 40+ node types across 9 categories, color maps,
      `requiresChain` for Tier-2 economic nodes, `categoryFor()` helper.

## Phase C — Render primitives
- [x] `quest-node.tsx` — custom node, target(top)/source(bottom) handles,
      category accent, run-state ring + badge.
- [x] `layout.ts` — longest-path layering + horizontal spread; cycle-safe
      (iteration cap). Fixed Map-iteration TS target issue (`forEach`, not
      `for...of`).
- [x] `dag-flow.tsx` — read-only graph for My Quests; conditional edges animated
      + dashed amber; minimap colored by category hex.

## Phase D — Interactive builder
- [x] `quest-canvas.tsx` — `ReactFlowProvider` wrapper; `useNodesState`/
      `useEdgesState`; palette (built-ins + templates, searchable, grouped);
      click-add + HTML5 drag-drop via `screenToFlowPosition`; `onConnect` edges;
      inspector with live JSON validation; auto-layout/clear toolbar;
      `BuiltGraph` serializer (node id → index map for edges).

## Phase E — Template creators + page rewire
- [x] `node-template-creator.tsx` — type-aware default-config seeding, 4 JSON
      fields validated before POST.
- [x] Rewired `quests/page.tsx`: My Quests→DagFlow, Builder→QuestCanvas,
      Quest Templates browse+create (canvas reused), Node Templates browse+create.
      `useNodeTemplates()` hook feeds the palette; created quests bounce back to
      My Quests tab.

## Phase F — Verify
- [x] `npx tsc --noEmit` filtered to new files: one Map-iteration error in
      `layout.ts`, fixed; re-run clean.
- [x] React Flow named exports confirmed resolvable at runtime.
- [x] No unused imports in the new files.
