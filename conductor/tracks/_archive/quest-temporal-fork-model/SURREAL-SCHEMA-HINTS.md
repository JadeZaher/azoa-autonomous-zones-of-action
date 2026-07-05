# SURREAL-SCHEMA-HINTS — Quest Tables & RELATE Edges

**Status:** Hand-off from `quest-temporal-fork-model` to `surrealdb-migration`.
**Consumed by:** `surrealdb-migration` plan tasks **3** (quest portion of
value-table schemas), **9** (graph remodel), and the quest portion of **10**
(holon polyhierarchy — only because `Quest` schema may move shared columns
adjacent to holon tables).

> This is a **documentation** file. It is not a `.surql` file and must
> not be executed by `Persistence/SurrealDb/Schemas/` loaders. The
> `surrealdb-migration` track materializes the schemas below verbatim
> as numbered `.surql` files in `Persistence/SurrealDb/Schemas/`.
> The wave-1 acceptance gate (section 5) bans quest table names from
> `.surql` files until that track lands.

---

## Table inventory

| # | Table | Layer | Owns |
| --- | --- | --- | --- |
| 1 | `quest` | Definition | Workflow shape (name, avatar, template ancestry, dapp series) |
| 2 | `quest_node` | Definition | Step inside a quest (type, config, entry/terminal flags, ExecutionOrder) |
| 3 | `quest_edge` | Definition | Control-flow edge between nodes inside one quest |
| 4 | `quest_run` | Runtime | One execution attempt of a quest |
| 5 | `quest_node_execution` | Runtime | Per-`(run, node)` state, output, error |

## RELATE edges

| Edge | Cardinality | Direction | Purpose |
| --- | --- | --- | --- |
| `forked_from` | many-to-one | `quest_run → quest_run` | Lineage: which parent run this fork branched from |
| `executes` | many-to-many | `quest_run → quest_node_execution` | A run owns its per-node execution rows; a single execution row may be referenced by multiple runs when a fork copies-by-reference for `ExecutionOrder < forkPoint` |

`forked_from` is **distinct** from `quest_run.parent_run_id` only as an
optimization for native SurrealDB graph traversal (`->forked_from->`).
The `parent_run_id` scalar is the authoritative pointer; the edge
mirrors it for cheap multi-hop ancestor walks. Both must be kept in
sync at write time.

`executes` is **the** carrier of the "copy-by-reference for nodes with
`ExecutionOrder < forkPoint`" semantic from
[`ADR.md`](ADR.md) §2.3. When a fork happens, the child run gains
`executes` edges to the parent's existing `quest_node_execution` rows
for already-completed nodes; no row duplication, no recompute.

---

## 1. `quest` (definition)

```surql
DEFINE TABLE quest SCHEMAFULL;

DEFINE FIELD id                ON TABLE quest TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD avatar_id         ON TABLE quest TYPE record<avatar>;
DEFINE FIELD name              ON TABLE quest TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD description       ON TABLE quest TYPE option<string>;
DEFINE FIELD template_id       ON TABLE quest TYPE option<record<quest_template>>;
DEFINE FIELD dapp_series_id    ON TABLE quest TYPE option<string>;
DEFINE FIELD metadata          ON TABLE quest TYPE object;
DEFINE FIELD created_date      ON TABLE quest TYPE datetime;
```

**Removed from `quest` (vs. legacy `Quest.cs`):**
- `status` — runtime concern, moved to `quest_run.status`.
- `completed_date` — runtime concern, moved to `quest_run.ended_at`.

**Indexes:**

```surql
DEFINE INDEX quest_avatar_id      ON TABLE quest FIELDS avatar_id;
DEFINE INDEX quest_template_id    ON TABLE quest FIELDS template_id;
DEFINE INDEX quest_dapp_series    ON TABLE quest FIELDS dapp_series_id;
```

---

## 2. `quest_node` (definition)

```surql
DEFINE TABLE quest_node SCHEMAFULL;

DEFINE FIELD id                ON TABLE quest_node TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD quest_id          ON TABLE quest_node TYPE record<quest>;
DEFINE FIELD node_template_id  ON TABLE quest_node TYPE option<record<quest_node_template>>;
DEFINE FIELD node_type         ON TABLE quest_node TYPE string;
DEFINE FIELD name              ON TABLE quest_node TYPE string;
DEFINE FIELD config            ON TABLE quest_node TYPE string;       -- JSON-serialized request model
DEFINE FIELD is_entry          ON TABLE quest_node TYPE bool DEFAULT false;
DEFINE FIELD is_terminal       ON TABLE quest_node TYPE bool DEFAULT false;
DEFINE FIELD execution_order   ON TABLE quest_node TYPE int;
```

**Removed from `quest_node` (vs. legacy `QuestNode.cs`):**
- `state` — moved to `quest_node_execution.state`.
- `output` — moved to `quest_node_execution.output`.
- `error` — moved to `quest_node_execution.error`.

**Indexes:**

```surql
DEFINE INDEX quest_node_quest_id   ON TABLE quest_node FIELDS quest_id;
-- (quest_id, execution_order) — used by topological sort & fork-point selection
DEFINE INDEX quest_node_order      ON TABLE quest_node FIELDS quest_id, execution_order;
```

---

## 3. `quest_edge` (definition)

```surql
DEFINE TABLE quest_edge SCHEMAFULL;

DEFINE FIELD id              ON TABLE quest_edge TYPE string;
DEFINE FIELD quest_id        ON TABLE quest_edge TYPE record<quest>;
DEFINE FIELD source_node_id  ON TABLE quest_edge TYPE record<quest_node>;
DEFINE FIELD target_node_id  ON TABLE quest_edge TYPE record<quest_node>;
DEFINE FIELD condition       ON TABLE quest_edge TYPE option<string>;
DEFINE FIELD edge_type       ON TABLE quest_edge TYPE string
    ASSERT $value INSIDE ['Control', 'Conditional'];
```

**Indexes:**

```surql
DEFINE INDEX quest_edge_quest_id   ON TABLE quest_edge FIELDS quest_id;
DEFINE INDEX quest_edge_source     ON TABLE quest_edge FIELDS source_node_id;
DEFINE INDEX quest_edge_target     ON TABLE quest_edge FIELDS target_node_id;
```

> Optional optimization for `surrealdb-migration`: replace `quest_edge`
> with a native `RELATE source_node -> control_flow -> target_node` edge
> table. Either shape preserves the intra-iteration DAG semantics; the
> scalar form above is the conservative default that keeps
> `QuestDagValidator` queries unchanged.

---

## 4. `quest_run` (runtime — canonical lineage carrier)

```surql
DEFINE TABLE quest_run SCHEMAFULL;

DEFINE FIELD id                 ON TABLE quest_run TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD quest_id           ON TABLE quest_run TYPE record<quest>;
DEFINE FIELD avatar_id          ON TABLE quest_run TYPE record<avatar>;

DEFINE FIELD status             ON TABLE quest_run TYPE string
    ASSERT $value INSIDE ['Pending','Running','Succeeded','Failed','Forked','Cancelled'];

DEFINE FIELD started_at         ON TABLE quest_run TYPE datetime;
DEFINE FIELD ended_at           ON TABLE quest_run TYPE option<datetime>;

-- Lineage (tree — see ADR §2.4)
DEFINE FIELD parent_run_id      ON TABLE quest_run TYPE option<record<quest_run>>;
DEFINE FIELD forked_at_node_id  ON TABLE quest_run TYPE option<record<quest_node>>;
DEFINE FIELD fork_reason        ON TABLE quest_run TYPE option<string>;

-- Supervisor-driven failure audit
DEFINE FIELD fail_reason        ON TABLE quest_run TYPE option<string>;
```

**Indexes:**

```surql
DEFINE INDEX quest_run_quest_id    ON TABLE quest_run FIELDS quest_id;
DEFINE INDEX quest_run_avatar_id   ON TABLE quest_run FIELDS avatar_id;
DEFINE INDEX quest_run_status      ON TABLE quest_run FIELDS status;
DEFINE INDEX quest_run_parent      ON TABLE quest_run FIELDS parent_run_id;
```

**State machine invariants** (enforced in `QuestManager`, not in the DB):
- `Pending → Running` — first node claim succeeds.
- `Running → Succeeded` — all terminal nodes report `Succeeded`.
- `Running → Failed` — any node reports `Failed` (or supervisor calls
  `MarkRunFailedAsync` with `fail_reason`).
- `Running → Forked` — fork API succeeds; parent transitions to terminal
  `Forked`; child created `Pending`.
- `Running → Cancelled` — explicit cancellation.
- `Succeeded`/`Failed`/`Forked`/`Cancelled` are **terminal**. No further
  transitions.

---

## 5. `quest_node_execution` (runtime — per-(run, node))

```surql
DEFINE TABLE quest_node_execution SCHEMAFULL;

DEFINE FIELD id            ON TABLE quest_node_execution TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD run_id        ON TABLE quest_node_execution TYPE record<quest_run>;
DEFINE FIELD node_id       ON TABLE quest_node_execution TYPE record<quest_node>;

DEFINE FIELD state         ON TABLE quest_node_execution TYPE string
    ASSERT $value INSIDE ['Pending','Running','Succeeded','Failed','Skipped','Cancelled'];

DEFINE FIELD output        ON TABLE quest_node_execution TYPE option<string>;  -- JSON-serialized AZOAResult<T>
DEFINE FIELD error         ON TABLE quest_node_execution TYPE option<string>;

DEFINE FIELD started_at    ON TABLE quest_node_execution TYPE datetime;
DEFINE FIELD ended_at      ON TABLE quest_node_execution TYPE option<datetime>;
```

**Indexes:**

```surql
-- Natural key: one row per (run, node) — exact-match lookup
DEFINE INDEX quest_node_execution_run_node
    ON TABLE quest_node_execution
    FIELDS run_id, node_id
    UNIQUE;

DEFINE INDEX quest_node_execution_run    ON TABLE quest_node_execution FIELDS run_id;
DEFINE INDEX quest_node_execution_node   ON TABLE quest_node_execution FIELDS node_id;
DEFINE INDEX quest_node_execution_state  ON TABLE quest_node_execution FIELDS state;
```

**G2 (api-safety-hardening) claim primitive**: the
`TryClaimPendingAsync(runId, nodeId)` store method performs a conditional
update that only succeeds when current `state == 'Pending'`. In SurrealDB
this maps to:

```surql
UPDATE quest_node_execution
   SET state = 'Running', started_at = time::now()
   WHERE run_id = $run_id
     AND node_id = $node_id
     AND state = 'Pending'
   RETURN AFTER;
```

The empty-result case = lost race (already claimed by another worker).
This matches the `UpdateOnly-Where-Set` pattern produced by Owner A's
Phase 3 parameterized-query builder.

---

## 6. RELATE edges (graph layer)

### 6.1 `forked_from` — lineage edge

```surql
DEFINE TABLE forked_from SCHEMAFULL TYPE RELATION FROM quest_run TO quest_run;

-- (no extra fields on the edge — the scalar `parent_run_id` on quest_run
--  carries the same information for non-graph queries; the edge enables
--  cheap multi-hop ancestor walks like `->forked_from->forked_from->...`)
```

**Write contract:** every time a fork creates a new `quest_run` with
`parent_run_id = X`, also `RELATE quest_run:NEW -> forked_from ->
quest_run:X` is created in the same SurrealDB transaction.

**Lineage query** (`GetLineageAsync(runId)`):

```surql
SELECT * FROM quest_run:$id WHERE true
FETCH parent_run_id.parent_run_id.parent_run_id...
-- or, native graph traversal:
SELECT ->forked_from->quest_run.* FROM quest_run:$id;
```

The store interface (`IQuestRunStore.GetLineageAsync`) returns the
ancestor chain in **child-to-root** order (caller can `.Reverse()` for
root-to-child if desired).

### 6.2 `executes` — copy-by-reference for fork pre-history

```surql
DEFINE TABLE executes SCHEMAFULL TYPE RELATION FROM quest_run TO quest_node_execution;
```

**Write contract:**
1. When a node execution is first created for a run, `RELATE run ->
   executes -> exec` is created alongside the row.
2. When a fork happens, for every `quest_node_execution` where
   `node_id.execution_order < forkPoint`, an additional `RELATE
   child_run -> executes -> exec` is created. The exec row is **not**
   duplicated.

**Why a RELATE edge instead of a scalar:** the relationship is
many-to-many (one execution row, many runs that reference it) and the
native graph form is the only way to express that without an
intermediate join table. Scalar `run_id` on `quest_node_execution`
would force duplication on fork.

---

## 7. Removed tables (legacy)

None. `quest`, `quest_node`, `quest_edge` retain their names; they just
shed their runtime columns (see §§1–3 "Removed from" subsections).
`quest_run` and `quest_node_execution` are **new**.

`quest_template`, `quest_template_node`, `quest_template_edge`,
`quest_node_template`, `quest_dependency` are unchanged by this track
and are listed in `surrealdb-client-package` for separate schema work.

---

## 8. Acceptance checklist for `surrealdb-migration` task 9

When `surrealdb-migration` task 9 lands, the following must be true:

- [ ] `Persistence/SurrealDb/Schemas/NNN_quest.surql` exists with
      §1 contents.
- [ ] `Persistence/SurrealDb/Schemas/NNN_quest_node.surql` exists with
      §2 contents.
- [ ] `Persistence/SurrealDb/Schemas/NNN_quest_edge.surql` exists with
      §3 contents (or the optional RELATE-based form).
- [ ] `Persistence/SurrealDb/Schemas/NNN_quest_run.surql` exists with
      §4 contents.
- [ ] `Persistence/SurrealDb/Schemas/NNN_quest_node_execution.surql`
      exists with §5 contents.
- [ ] `forked_from` RELATION table defined (§6.1).
- [ ] `executes` RELATION table defined (§6.2).
- [ ] `(run_id, node_id)` UNIQUE index on `quest_node_execution`.
- [ ] All tables `SCHEMAFULL`, all fields `TYPE`'d (G6 schema-shape
      invariant from wave-1 gate section 6).
- [ ] File names match `^[0-9]{3}_[a-z][a-z0-9_]*\.surql$` (gate section 6).
- [ ] Wave-1 gate section 5(a) re-runs cleanly **only after** the
      wave-1 base SHA file (`scripts/surrealdb/.wave-1-base-sha`) is
      advanced past this track's commits — the section 5(b) git-diff
      window then excludes `Models/Quest/*` changes that this track
      introduces.
