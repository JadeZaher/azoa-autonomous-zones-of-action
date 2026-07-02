---
type: plan
track: quest-dag-semantic-hardening
created: 2026-07-01
status: pending
---

# Implementation Plan: quest-dag-semantic-hardening

## Overview

Five phases. A = the P0 skip-semantics bug (self-contained, highest value).
B = publish gate + engine-profile check (they interlock: the gate is where the
profile check gets its authority). C = config-schema validation + safe
deserialization (the publish gate from B calls into C's registry, but C is
written to be callable standalone so B can land with a stub hook).
D = the two scoped guards (holon-ref scanner + parent-cycle). E = the SINGLE
integrated build+test sweep (user test-execution policy: write tests throughout,
run them once at the end — do NOT iterate test→fix per phase; phase-end
checkpoints are review/static checks only).

TDD-light per workflow.md: for each task, write the pinning test in
`tests/AZOA.WebAPI.Tests` (xUnit + FluentAssertions + Moq, Builder pattern per
existing `IntegrationTests/Builders/TestDataBuilders.cs` style) alongside the
implementation; defer execution to Phase E.

Commits: `[quest-dag-semantic-hardening] <imperative>`, roughly one per phase.

## Phase A: Skip-propagation semantics (P0 — Item 1)

Goal: a failed gate stops the entire chain behind it; no inert Conditional edges.

Tasks:

- [x] Task A1: Write pinning tests for cascade-skip (TDD red first).
      New test class `tests/AZOA.WebAPI.Tests/Quest/QuestManagerSkipPropagationTests.cs`:
      (1) GateCheck→Transfer1→Transfer2 all-Control chain, gate fails ⇒ BOTH Skipped (AC-1a);
      (2) Conditional edge with EMPTY condition, source Failed ⇒ target Skipped (AC-1b);
      (3) Conditional edge source Skipped ⇒ target Skipped; (4) happy-path all-Succeeded.
      No existing tests pinned one-hop-only skip by assertion (confirmed by grep).
- [x] Task A2: Implement new skip rule in `Managers/QuestManager.cs` skip loop:
      Control skips when source Failed OR Skipped; Conditional skips on Failed/Skipped
      regardless of Condition text. G2 guard untouched.
- [x] Task A3: `Validators/QuestEdgeCreateModelValidator.cs` — Conditional requires
      non-empty Condition. New `Validators/QuestEdgeAddModelValidator.cs` for post-hoc
      surface. Validation test cases added to `ValidationTests.cs`.
- [x] Task A4: Durable path confirmed: no skip seam (ResolveSingleSuccessor/saga compensation).
      Divergence documented in `Services/Quest/Workflow/AGENTS.md §skip-semantics`.
- [x] Verification A: AC-1a/1b/1c/1d all covered. No other call sites read the old
      Conditional guard (the only site was QuestManager.cs:308, now updated). [checkpoint]

## Phase B: Publish gate + engine-profile validation (Items 2+3)

Goal: Draft→Active is the moment validation becomes an invariant; the durable
engine can never start a fan-out quest.

Tasks:

- [ ] Task B1: Reintroduce definition lifecycle status. Add `Status`
      (`QuestStatus`, default `Draft`) to the domain Quest
      (`Models/Quest/Quest.cs`) and the POCO
      (`Persistence/SurrealDb/Models/Quest.cs`) — the POCO's SurrealNote records
      status was removed by quest-temporal-fork-model; this is a deliberate
      reintroduction for the DEFINITION lifecycle (update the note). `[Inside]`
      constraint mirrors the FULL domain `QuestStatus` enum
      (Draft/Active/Completed/Failed/Archived) per the
      quest-run-status-inside-constraint-gap lesson. Regen schema goldens via
      the pipeline — never hand-edit `.surql`. Ensure the store round-trips the
      field (`SurrealQuestStore`).
- [ ] Task B2: Engine-profile check (Item 3, AC-3a/3b). Add `Warnings` list to
      `DagValidationResult`; add a fan-out check (any node with >1 outgoing
      Control edges) — either inside `Services/QuestDagValidator.cs` behind a
      profile flag or as a small helper both callers share (keep it a targeted
      check, NO new abstraction). Publish gate + `StartWorkflowRunAsync`
      (`Managers/QuestManager.cs:1257`) treat fan-out as ERROR;
      `ExecuteAsync` (line 244 validation) records it as WARNING only. Unit
      tests: fan-out quest → error via workflow path, warning via legacy path.
- [ ] Task B3: Publish/unpublish manager methods. `PublishAsync(questId, avatarId)`:
      owned-quest load (reuse `LoadOwnedQuestAsync` IDOR pattern) → full stack
      (structural `QuestDagValidator` + B2 profile-as-error + FR-4 config check
      via a hook that Phase C fills in — wire the call now, registry lands in C)
      → persist fresh `ExecutionOrder` → `Draft→Active`.
      `UnpublishAsync(questId, avatarId)`: refuse while any `QuestRun` for the
      quest is non-final (query `_runStore`); else `Active→Draft`. Unit tests
      for AC-2a/2d.
- [ ] Task B4: Enforce lifecycle at seams (AC-2b/2c). `ExecuteAsync` +
      `StartWorkflowRunAsync` require `Status == Active` (clear error naming
      publish); keep execute-time re-validation as defense in depth. Definition
      mutations — `AddNodeAsync` (:866), `UpdateNodeAsync` (:894), node delete,
      `AddEdgeAsync` (:958), `RemoveEdgeAsync` (:1015) — reject when
      `Status == Active` with a conflict-style error. Unit tests: mutate-Active
      rejected, mutate-after-unpublish succeeds.
- [ ] Task B5: Controller endpoints `POST /api/quest/{id}/publish` +
      `POST /api/quest/{id}/unpublish` on `Controllers/QuestController.cs`
      (matches existing verb-subroute shape, cf. `/{id}/validate` :88,
      `/{id}/execute` :98). Map conflict-style manager errors appropriately.
      Note in AGENTS.md (Managers/ or Services/Quest/) §publish-lifecycle.
- [ ] Task B6: Sweep existing tests/fixtures that create-and-execute quests —
      builders must publish (or construct `Active`) before execute, or the whole
      suite fails in Phase E. Update `TestDataBuilders` accordingly.
- [ ] Verification B: self-review against AC-2a–2e + AC-3a/3b; confirm goldens
      regenerated (diff shows generated `.surql`, no hand edits)
      [checkpoint marker]

## Phase C: Config schema validation + safe deserialization (Item 4)

Goal: malformed config fails a node cleanly at run time and is rejected outright
at definition time; no `Deserialize<T>(...)!` remains in handlers.

Tasks:

- [ ] Task C1: Shared helper — `Services/Quest/QuestNodeConfig.cs` with
      `TryDeserialize<T>(string? json, out T config, out string error)` (or a
      small result type): strict `System.Text.Json` options
      (`UnmappedMemberHandling.Disallow` where feasible), null/empty and
      `JsonException` handled, error message carries node-type + parse detail.
      Unit tests: malformed JSON, unknown member, empty string, happy path.
- [ ] Task C2: Registry `QuestNodeType → config DTO type` (DTOs in
      `Models/Quest/NodeConfigs.cs`); config-free node types registered
      explicitly. Unit test: every `QuestNodeType` enum value has a registry
      entry (exhaustiveness pin — new node types can't dodge it).
- [ ] Task C3: Handler sweep — replace `JsonSerializer.Deserialize<T>(...)!` in
      ALL `Services/Quest/Handlers/*.cs` (29 sites; `GateCheckNodeHandler`
      already catches — converge it on the helper too) with the C1 helper;
      failure returns `QuestNodeResults.Fail`, never throws. Mechanical,
      one pattern. Tests (AC-4a): one Tier-2 handler (Transfer — silent-wrong
      defaulted amount case) + one Tier-1 handler with malformed config ⇒
      Failed execution with descriptive message.
- [ ] Task C4: Definition-time enforcement (AC-4b): `AddNodeAsync`/
      `UpdateNodeAsync` validate config via registry strict round-trip; wire the
      B3 publish-gate hook to re-check all nodes. Unit tests: bad config
      rejected at add/update; quest with a bad-config node fails publish.
- [ ] Task C5: Document in `Services/Quest/AGENTS.md` §node-config (helper is
      mandatory for new handlers; template configSchema/inputSchema/outputSchema
      explicitly remain unenforced — AC-4c).
- [ ] Verification C: grep confirms zero remaining `Deserialize<...>(...)!` in
      `Services/Quest/Handlers/`; registry exhaustiveness test present
      [checkpoint marker]

## Phase D: Scoped guards (Items 5+6)

Tasks:

- [ ] Task D1 (Item 5, AC-5): extend `ScanForGuids` in
      `Managers/DappCompositionManager.cs:529+` to also collect Guids from
      ARRAY values under `*holon*`-named properties (covers
      `GateCheckNodeConfig.Holons`, `Models/Quest/NodeConfigs.cs:100`) — or, if
      smaller after C2 lands, extract via the typed registry for known types.
      Regression test: GateCheck node referencing a nonexistent holon ⇒
      `HolonBindingsResolved=false` + diagnostic.
- [ ] Task D2 (Item 6, AC-6a/6b): extract the descendant-cycle check from
      `Managers/HolonManager.cs:393–398` (`MoveSubtreeAsync`) into a shared
      private guard (e.g. `EnsureNotDescendantAsync(id, proposedParentId)`) and
      call it from every `ParentHolonId` write: `CreateAsync` (~:38, when
      supplied), `UpdateAsync` (~:61), `InteractAsync` (~:104–105
      `NewParentHolonId`), and `MoveSubtreeAsync` itself. Clear rejection
      message. Tests: A-parent-B then B-parent-A rejected on each of the four
      paths; ancestors complete on surviving fixtures.
- [ ] Verification D: self-review both guards; confirm no other
      `ParentHolonId =` write sites exist (grep) [checkpoint marker]

## Phase E: Single integrated verification sweep

Goal: the ONE test/build run for the whole track (user test-execution policy).

Tasks:

- [ ] Task E1: `dotnet build` — zero errors, zero NEW warnings vs the
      28-warning baseline (build-warning-baseline-2026-06-16).
- [ ] Task E2: `dotnet test` — full sweep: unit (`tests/AZOA.WebAPI.Tests`) +
      schema tests (golden regen from B1) + integration (SurrealDB up; expect
      the B6 fixture updates to carry the suite). Fix any failures surfaced,
      then re-run the sweep to green — this is the only iterate-on-tests loop
      in the track.
- [ ] Task E3: Docs closeout — AGENTS.md sections (skip semantics, publish
      lifecycle, node-config) present; spec Out-of-Scope list mirrored as
      follow-up notes; optional: note the frontend `dagWarnings` mirror as a
      named follow-up, no frontend code (do NOT run frontend typecheck per
      no-frontend-typecheck).
- [ ] Verification E: quality gates per workflow.md — build zero-new-warnings,
      tests green, Swagger lists publish/unpublish; evidence recorded for
      reviewer [checkpoint marker]
