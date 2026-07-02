---
type: spec
track: quest-dag-semantic-hardening
created: 2026-07-01
status: pending
---

# Track: quest-dag-semantic-hardening

## Overview

**Bug/hardening (mixed): P0 skip-semantics bug + five semantic-validation hardening
items.** The quest engine validates DAG *shape* well
([Services/QuestDagValidator.cs](../../../Services/QuestDagValidator.cs) — Kahn's
cycle detection, entry/terminal/orphan checks) but almost nothing about *meaning*,
and validation is a point-in-time event rather than a maintained invariant. A
two-round architecture/security review (2026-07-01) surfaced six concrete gaps;
all decisions below are **locked** — no open questions.

The P0 item is a live-money hazard: a failed `GateCheck` stops only its immediate
successor. Skip does not cascade, so a payout chain behind a failed eligibility
gate **runs anyway** from the second hop on.

Greenfield pre-launch ([greenfield-prelaunch-no-compat](../../../conductor/tracks.md)):
semantics change outright, no compat shims; tests that deliberately pinned the old
behavior are updated, not preserved.

## Background (verified findings)

| # | Gap | Evidence |
|---|---|---|
| 1 | **P0 — skip does not propagate.** Control edge skips target only when source `Failed`; a `Skipped` source lets the target run. Conditional edge with empty `Condition` text is completely inert (neither branch applies). | [Managers/QuestManager.cs](../../../Managers/QuestManager.cs):296–322 (`ExecuteAsync` skip loop): `Control && sourceState == Failed` only; `Conditional && !IsNullOrEmpty(Condition)` guard. Trace: GateCheck →(Control) Transfer1 →(Control) Transfer2 — gate fails → Transfer1 Skipped → **Transfer2 runs**. |
| 2 | **Validation is not an invariant.** `_dagValidator.Validate` runs only at `POST /{id}/validate`, `ExecuteAsync` (line 244), `StartWorkflowRunAsync` (line 1257+), `GetTopologicalOrderAsync`. `CreateAsync` (line 97) persists with FluentValidation only — a cyclic quest saves fine. `AddNodeAsync`/`UpdateNodeAsync`/`RemoveEdgeAsync` do no graph validation; `AddEdgeAsync` cycle-checks only. `ExecutionOrder` goes stale after mutations. **Note:** the Quest *definition* currently has NO status field — `status` was intentionally removed from the `quest` POCO by quest-temporal-fork-model (see `Persistence/SurrealDb/Models/Quest.cs` SurrealNote); `QuestStatus` (Draft/Active/Completed/Failed/Archived) exists in `Models/Quest/QuestEnums.cs` but is unpersisted for definitions. |
| 3 | **Engine-profile fan-out divergence.** Durable engine hard-rejects >1 outgoing Control edge at runtime ([Services/Quest/Workflow/QuestWorkflowEdges.cs](../../../Services/Quest/Workflow/QuestWorkflowEdges.cs):34–42 `ResolveSingleSuccessor` → `SuccessorKind.FanOut`); legacy engine runs fan-out fine in topo order; validator accepts both. Same stored quest: valid + runnable on one engine, guaranteed mid-run failure on the other. |
| 4 | **Config is schema-unchecked + unsafely deserialized.** Node `Config` is a raw JSON string (≤64KB) validated only as "is a string". 29/30 handlers in `Services/Quest/Handlers/` call `JsonSerializer.Deserialize<T>(...)!` — malformed config throws mid-run; missing fields silently default (a Transfer with defaulted target/amount is silent-wrong). Only `GateCheckNodeHandler` catches. |
| 5 | **`ExtractBoundHolonIds` naming hole.** [Managers/DappCompositionManager.cs](../../../Managers/DappCompositionManager.cs):503–527 scans Config JSON for *string* properties named `*holon*` (case-insensitive) holding a Guid. `GateCheckNodeConfig.Holons` ([Models/Quest/NodeConfigs.cs](../../../Models/Quest/NodeConfigs.cs):100) is a `List<Guid>` — array values are never collected, so gate-check holon refs are invisible to composition validation. |
| 6 | **Holon parent-cycle guard is move-only.** `HolonManager.MoveSubtreeAsync` ([Managers/HolonManager.cs](../../../Managers/HolonManager.cs):393–398) checks descendant-cycle; `CreateAsync` (line ~38), `UpdateAsync` (line ~61), and `InteractAsync` (lines ~104–105, `NewParentHolonId`) write `ParentHolonId` with no check — A-parent-B then B-parent-A persists a cycle. Traversals won't hang (visited sets) but `GetAncestorsAsync` silently returns truncated ancestry. |

## Functional Requirements

### FR-1 (P0): Skip-propagation semantics — cascade skip, no inert edges

**Locked fix** in the `ExecuteAsync` skip loop (`Managers/QuestManager.cs:296–322`):

- (a) **Control edges propagate skip:** target is skipped if ANY Control-edge
  source is `Failed` **or `Skipped`**.
- (b) **Conditional edges skip on source `Failed`/`Skipped` regardless of
  condition text** — the `!string.IsNullOrEmpty(edge.Condition)` guard is removed
  from the skip decision.
- (c) **Input-layer belt-and-braces:** edge input validation requires non-empty
  `Condition` when `EdgeType == Conditional` — in
  `Validators/QuestEdgeCreateModelValidator.cs` AND on the `AddEdgeAsync` /
  `QuestEdgeAddModel` surface (no validator exists for the add model today; add
  the rule wherever that surface validates).
- (d) **Durable-path check:** `Services/Quest/Workflow/QuestNodeStepHandler.cs` +
  `QuestWorkflowEdges.cs` follow single-Control-successor chains and handle
  failure via saga retry/compensation, not skip. If no equivalent skip seam
  exists, **document the divergence** in `Services/Quest/Workflow/` AGENTS.md
  (and this track's notes) and align only where cheap.

**Acceptance criteria:**

- AC-1a: GateCheck →(Control) Transfer1 →(Control) Transfer2, gate fails ⇒
  Transfer1 `Skipped` AND Transfer2 `Skipped` (test asserts the second hop).
- AC-1b: A Conditional edge with empty `Condition` behaves identically to one
  with condition text w.r.t. skip-on-Failed/Skipped (no inert path); and new
  edge input with `EdgeType=Conditional` + empty `Condition` is rejected at
  validation on both input surfaces.
- AC-1c: Divergence (or alignment) of the durable path is documented; if a skip
  seam exists there, it applies the same Failed-or-Skipped rule.
- AC-1d: Existing tests pinning one-hop-only skip are updated to the new
  semantics (deliberate change, called out in the commit body).

### FR-2: Draft→Active publish gate — validation as invariant

**Locked fix:** make `QuestStatus.Draft → Active` a real publish transition.

- Reintroduce a definition-lifecycle `Status` on the Quest definition
  (domain `Models/Quest/Quest.cs` + POCO `Persistence/SurrealDb/Models/Quest.cs`),
  default `Draft`. POCO enum/`[Inside]` constraint MUST mirror the full domain
  `QuestStatus` enum ([quest-run-status-inside-constraint-gap] lesson); **regen
  goldens, never hand-edit `.surql`**.
- New endpoints on `QuestController` matching its existing verb-subroute shape
  (`/{id}/validate`, `/{id}/execute`): `POST /api/quest/{id}/publish` and
  `POST /api/quest/{id}/unpublish`.
- **Publish** runs the FULL validation stack — structural DAG
  (`QuestDagValidator`) + engine-profile check (FR-3) + per-node config-schema
  check (FR-4) — and only on success flips `Draft → Active` (also persisting
  fresh `ExecutionOrder`).
- `ExecuteAsync` and `StartWorkflowRunAsync` require `Active`; execute-time
  re-validation is KEPT as defense in depth.
- Definition mutations (add/update/delete node, add/remove edge) on an `Active`
  quest are rejected with a conflict-style error; author must **unpublish**
  first. Unpublish is allowed only when no in-flight runs exist (no `QuestRun`
  in a non-final state for that quest).

**Acceptance criteria:**

- AC-2a: Publishing a quest with a structural error (e.g. cycle) fails and the
  quest stays `Draft`; publishing a valid quest flips to `Active`.
- AC-2b: `ExecuteAsync` / `StartWorkflowRunAsync` on a `Draft` quest return an
  error naming the publish requirement.
- AC-2c: `AddNodeAsync` / `UpdateNodeAsync` / node delete / `AddEdgeAsync` /
  `RemoveEdgeAsync` against an `Active` quest are rejected; the same calls
  succeed after unpublish.
- AC-2d: Unpublish is refused while an in-flight run exists; allowed once runs
  are final.
- AC-2e: SurrealDB schema goldens regenerated; `[Inside]` values mirror the full
  domain `QuestStatus` enum; schema tests green.

### FR-3: Engine-profile validation (fan-out)

**Locked fix — targeted check, no new abstraction:**

- `DagValidationResult` gains `Warnings` (and minimal profile info).
- A fan-out check (any node with >1 outgoing **Control** edges) is invoked from
  `StartWorkflowRunAsync` and the FR-2 publish gate as an **error**; the legacy
  `ExecuteAsync` path treats it as a **warning only** (fan-out remains legal
  there).

**Acceptance criteria:**

- AC-3a: A quest with a 2-Control-out node fails `StartWorkflowRunAsync`
  validation with a clear fan-out error (before any saga rows are written) and
  fails publish.
- AC-3b: The same quest still executes on the legacy `ExecuteAsync` path,
  surfacing a warning in the validation result.

### FR-4: Per-node-type config schema validation + safe deserialization

**Locked fix — typed-DTO round-trip IS the validation; no JSON-Schema engine:**

- (a) A shared helper (e.g. `Services/Quest/QuestNodeConfig.TryDeserialize<T>`
  or equivalent result-returning API) used by ALL handlers in
  `Services/Quest/Handlers/` — malformed/missing config returns
  `QuestNodeResults.Fail` with a clear message (node type + parse error), never
  an unhandled exception. Replaces every `JsonSerializer.Deserialize<T>(...)!`.
- (b) Definition-time validation: a registry mapping `QuestNodeType` → config
  DTO type (DTOs live in `Models/Quest/NodeConfigs.cs`). At node create/update
  AND at the publish gate, strict deserialization
  (`System.Text.Json` with `UnmappedMemberHandling.Disallow` where feasible)
  rejects malformed/unknown-member config.
- Node types with no config DTO (if any) pass through the registry explicitly
  (registered as config-free), not by accident.

**Acceptance criteria:**

- AC-4a: Every handler routed through the shared helper; a run whose node has
  malformed config produces a `Failed` node execution with a descriptive
  message — no unhandled `JsonException` (test at least one Tier-2 handler,
  e.g. Transfer, plus one Tier-1).
- AC-4b: `AddNodeAsync`/`UpdateNodeAsync` reject config that fails strict
  round-trip for the node's type; publish gate re-checks all nodes.
- AC-4c: `QuestNodeTemplate.configSchema`/`inputSchema`/`outputSchema` remain
  unenforced — explicitly out of scope (documented, not silently ignored).

### FR-5: ExtractBoundHolonIds collects Guid arrays

**Locked fix:** extend the scanner at `Managers/DappCompositionManager.cs:503–527`
(`ScanForGuids`) to also collect Guid values from **arrays** under properties
named `*holon*` (case-insensitive) — covering `GateCheckNodeConfig.Holons` — or
switch extraction for known node types to the FR-4 typed registry, whichever is
smaller.

**Acceptance criteria:**

- AC-5: Regression test — a composition containing a GateCheck node whose
  `Holons` list references a nonexistent holon now reports
  `HolonBindingsResolved = false` with a diagnostic (previously invisible).

### FR-6: Holon parent-cycle guard on every ParentHolonId write

**Locked fix:** extract the `MoveSubtreeAsync` descendant-cycle check
(`Managers/HolonManager.cs:395–398`) into a shared guard and apply it on every
path that writes `ParentHolonId`: `CreateAsync` (when `ParentHolonId` supplied),
`UpdateAsync` (parent change), `InteractAsync` (`NewParentHolonId`). Reject with
a clear error message.

**Acceptance criteria:**

- AC-6a: A-parent-B followed by an attempt to set B as parent of A (via create,
  update, interact, or move) is rejected on every path; no cycle persists.
- AC-6b: `GetAncestorsAsync` on any surviving fixture returns complete ancestry
  (no silent truncation scenario reachable through the API).

## Non-Functional Requirements

- **NFR-1 (build hygiene):** `dotnet build` with zero NEW warnings vs the
  28-warning baseline ([build-warning-baseline-2026-06-16]).
- **NFR-2 (test policy):** all fixes land first; ONE integrated
  `dotnet build` + `dotnet test` sweep at the end (user test-execution policy).
  Coverage per workflow.md (>70% manager/service logic on touched code).
- **NFR-3 (docs convention):** terse one-line doc-comments; rationale goes in
  directory-level AGENTS.md sections (skip semantics in `Managers/` or
  `Services/Quest/` AGENTS.md; publish lifecycle likewise).
- **NFR-4 (schema discipline):** any `Persistence/SurrealDb/Models/` change
  regenerates goldens via the pipeline; `.surql` never hand-edited.
- **NFR-5 (commits):** `[quest-dag-semantic-hardening] <imperative>`.

## Out of Scope (follow-ups — listed, NOT planned here)

1. Output-binding/templating for Tier-2 node configs (dynamic amounts from
   upstream outputs).
2. First-class failure arm (`OnFailure` edge type / gate-else branch).
3. Emit webhook events (generalizing the consent outbox).
4. `QuestDependency` enforcement at execute time.
5. Holon metadata schema registry / AssetType vocabulary.
6. Frontend builder changes beyond (optionally) mirroring the new skip/publish
   rules in the advisory `dagWarnings` — frontend work minimal or zero.
7. `QuestNodeTemplate` stored `configSchema`/`inputSchema`/`outputSchema`
   enforcement (FR-4 AC-4c).

## Dependencies

None blocking. Builds on shipped economic-primitive-nodes,
durable-workflow-engine, quest-temporal-fork-model, dapp-composition.
