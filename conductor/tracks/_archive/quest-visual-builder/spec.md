# Track: quest-visual-builder

## Overview

Replace the raw JSON-textarea Quest authoring UI on the frontend Quest DAG page
with a **visual drag-and-drop workflow builder** built on React Flow
(`@xyflow/react` v12). The page exercises the already-shipped quest engine
(`quest-api`, `durable-workflow-engine`, `economic-primitive-nodes`) but its
authoring surface was a pair of JSON `<textarea>`s and the "DAG view" was a
numbered text list. This track makes the builder match the capability of the
engine underneath it.

Status: **Shipped 2026-06-20.** Built + type-verified in one session; this spec
is the as-built record. See [RETRO.md](RETRO.md).

## Goals

1. **Visual authoring** — drag/click nodes from a categorized palette onto a
   canvas, wire edges handle-to-handle, edit per-node config in an inspector.
2. **Real DAG visualization** — the "My Quests" detail view renders an actual
   graph (auto-laid-out, run-state-aware) instead of a text list.
3. **Template creation** — Quest Templates and Node Templates gain first-class
   create flows (previously read-only browse only).
4. **Palette driven by Node Templates** — node templates fetched from the API
   appear in the palette alongside built-ins, so authoring a node template
   immediately enriches the builder.
5. **No backend changes** — serialize to the exact `{nodes, edges}` index-ref
   shape the existing `POST /api/quest` / `/api/quest/templates` /
   `/api/quest/node-templates` endpoints already accept.

## Tech Stack

- Next.js 14 App Router (existing), Tailwind + shadcn/ui primitives (existing).
- `@xyflow/react` v12.11.0 — **only new dependency** (added to
  `frontend/package.json`).
- `@azoa/sdk` `azoa.api.request` for all calls (existing pattern).
- Dependency-free layered DAG auto-layout (no dagre/elkjs).

## Architecture

```
frontend/src/components/quest-builder/
  node-catalog.ts            -- static catalog mirroring backend QuestNodeType enum
                                (40+ types, 9 color-coded categories, seeded configs)
  quest-node.tsx             -- custom React Flow node (category accent, T/B handles,
                                run-state ring)
  layout.ts                  -- longest-path layered auto-layout (no extra deps)
  dag-flow.tsx               -- READ-ONLY graph render for "My Quests" detail
  quest-canvas.tsx           -- interactive builder: palette + canvas + inspector;
                                serializes to BuiltGraph {nodes, edges(index-ref)}
  node-template-creator.tsx  -- form -> POST /api/quest/node-templates
frontend/src/app/(dashboard)/quests/page.tsx
                             -- rewired: My Quests (DagFlow) / Builder (QuestCanvas)
                                / Quest Templates (browse + create) /
                                Node Templates (browse + create)
```

## Acceptance Criteria

- [x] AC1 — Builder tab: palette (built-ins + node templates), click/drag to add,
      drag-to-connect edges, inspector (name / Entry / Terminal / live-validated
      JSON config / delete), auto-layout + clear, minimap.
- [x] AC2 — My Quests DAG view renders a real React Flow graph with run-state
      coloring; replaced the old text-list `DagVisualizer`.
- [x] AC3 — Quest Templates tab gains a Create flow using the same canvas →
      `POST /api/quest/templates` (name/description/version/tags/public).
- [x] AC4 — Node Templates tab gains a Create flow → `POST /api/quest/node-templates`
      (type-aware default-config seeding, JSON-validated schemas).
- [x] AC5 — Palette is template-driven; node templates carry a `tpl` badge.
- [x] AC6 — `node-catalog.ts` mirrors `Models/Quest/QuestEnums.cs::QuestNodeType`
      1:1 (including economic Tier-2 `requiresChain` flag).
- [x] AC7 — Serialized graph matches existing API contract exactly; zero backend
      changes.
- [x] AC8 — `npx tsc --noEmit` clean across all new files + the rewritten page
      (scoped per [[no-frontend-typecheck]]).

## Out of Scope / Follow-ups

- Editing an existing quest's graph in place (builder is create-only;
  My Quests view is read-only).
- Schema-driven config forms (config is a JSON textarea; node-template
  `configSchema` is stored but not yet rendered as a form).
- Persisting canvas node positions server-side (layout is recomputed on load).
