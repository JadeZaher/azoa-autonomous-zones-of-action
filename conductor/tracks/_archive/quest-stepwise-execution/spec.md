---
type: spec
track: quest-stepwise-execution
created: 2026-07-06
status: shipped
horizon: post-launch
depends_on: [durable-workflow-engine, economic-primitive-nodes, quest-visual-builder]
---

# Quest Step-Wise Execution, Run-Scoped Data & Executability Validation — As-Built Retro

> **Shipped 2026-07-06.** Archived on completion. This is the as-built record, not a
> forward plan. Authoritative code notes live in `Services/Quest/AGENTS.md`
> (§output-binding, §output-schema, §executability-validation).

## Goal

Make quest DAGs (1) execute **step-by-step**, one node at a time, each completion an
event that triggers the next eligible node; (2) **preserve every node's output in the
run's state** so any downstream node can reuse it; (3) **refuse to validate/publish** a
DAG whose node would fail at runtime because its inputs aren't satisfiable; and
(4) **surface step-based execution in the frontend** quest area.

## Key finding: most of #1–#2 already existed

The durable saga path (`QuestNodeStepHandler`, "Approach A self-advancing handler" from
[durable-workflow-engine]) already ran **exactly one node per saga dispatch**, recorded
completion, resolved the single successor via `QuestWorkflowEdges.ResolveSingleSuccessor`,
and enqueued it; gate/timer nodes park (`AwaitingSignal`/`AwaitingTimer`) and resume on
`advance`/`signal`/timer. Per-node output already persisted on `QuestNodeExecution.Output`
and was already consumable by direct-edge successors via `{"$from":"upstream.<name>.<path>"}`.
So the work was **gap-fill**, not a rebuild.

## What shipped

### A. Run-scoped data reachability — new `run.` binding root
- `run.<nodeName>.<jsonPath>` reads **any prior node's** output by name (vs `upstream.` =
  direct-edge only). `GatePath.ValidRoots` → `["upstream","holon","run"]`.
- `QuestConfigBindingResolver.BuildRunScope` indexes ALL run executions by node name; both
  executors pass the full run-execution set (scope-building lives only in the resolver, so
  the legacy `ExecuteAsync` and durable `QuestNodeStepHandler` stay behaviorally mirrored).
- `QuestNodeStepHandler.LoadUpstreamAsync` → `LoadRunExecutionsAsync` returns both the
  direct-upstream map and the full run map from one store round-trip.

### B. Publish-time executability validation (un-defers AC-4c)
- `QuestNodeOutputSchema.cs` — declares the output shape of **all ~45 node types**.
  Serialization is **PascalCase + enums-as-numbers** (`QuestNodeJson.Options` sets no
  naming policy and no `JsonStringEnumConverter`). Wrapped nodes expose top-level
  `{IsError,Message,Result,Detail}` (Result=Object → deep paths admitted un-checked);
  Bridge/Back serialize the flat `BridgeTransactionResult`; GateCheck emits `{pass}`.
- `QuestDagExecutabilityValidator.cs` (in `PublishAsync`, after structural/transition/config
  validators): (A) ancestry — `upstream.`=direct pred, `run.`=**guaranteed ancestor via
  dominator dataflow**; (B) field presence (case-insensitive) against the schema; (C)
  best-effort scalar type match (fires only on provable scalar mismatch).
- Client-side mirror in `quest-canvas.tsx` (+ `node-output-schema.ts`) so authors see
  binding errors in the builder before publishing. Best-effort, never false-positives.

### C. Frontend Run panel
- New `'runs'` tab + `RunPanel` (`run-panel.tsx`): Start Run (`startWorkflow`) → poll
  `getExecutionState` (per-node state) → reuse `DagFlow`/`QuestNode` live overlay →
  advance (node-picker dropdown, defaults to last-Succeeded) / signal controls → per-node
  output inspector. The old fire-and-forget one-shot "Execute Quest" button was REMOVED —
  the durable run panel is the sole execution path.

## Hardening pass (code review, same day)

Three parallel reviews (correctness, security, quality). **Security verdict: LOW risk** —
run bindings are strictly single-run scoped at resolver + store; holon reads stay
owner-scoped with no oracle; every run endpoint enforces avatar ownership via
`LoadOwnedRunAsync`; publish is the un-bypassable executability chokepoint (Active quests
frozen against edits). Fixes applied:

| Fix | Severity | Note |
|-----|----------|------|
| Dominator `preds` = **Control edges only** | Critical | A node reachable only via Conditional/OnFailure was wrongly a "guaranteed ancestor". |
| **True-entry + Control-reachability strip** | Critical | 2nd bug: the first fix's entry-set heuristic (`zero Control-preds`) re-promoted conditional-only nodes to roots. Caught by the new regression tests, then fixed (entries = zero incoming of ANY type; non-Control-reachable nodes stripped from every dominator set). |
| **Node-name uniqueness gate** at publish | Critical | Name-keyed bindings: validator (first-match) vs resolver (last-writer-wins) could disagree; public-quest value-routing hazard. |
| **2-segment path guard** before `segments[2]` | High | `run.A` (no field) threw `IndexOutOfRange` when validator called directly. |
| **Size cap** (`MaxNodes`/`MaxEdges`) before dominator pass | Medium | Bounds the O(N²·E) pass — was a self-service CPU-exhaustion primitive. |
| React polling-effect deps tightened | Medium | Narrowed the blanket `exhaustive-deps` suppression. |

**Verified non-issues (not fixed):** enum→Number mapping (no string-enum converter,
confirmed); frontend per-click idempotency key (value-safety comes from server-derived
`{RunId}:{NodeId}` keys, not this key — it's cosmetic).

## Verification
- `dotnet build`: clean (baseline warnings only).
- Quest unit tests: **477 pass, 0 fail** (incl. 13 executability tests, 4 of them
  hardening regressions — 2 caught the dominator entry-set bug before merge).
- SDK `tsc` + `vitest`: clean (163 tests). Frontend NOT typechecked (project rule).

## Follow-ups (not blocking; deliberately deferred)
- Deep `Result.<field>` presence typing (validator admits Object-typed `Result` un-checked).
- `NodeOutputShape.None` has no live node yet (kept for future pure-side-effect handlers).
- Minor: resolver `upstream`/`run` branch dedup; RunPanel click-delegation via `data-id`.
