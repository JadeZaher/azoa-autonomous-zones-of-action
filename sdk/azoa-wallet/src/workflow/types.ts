/**
 * Workflow run-driver DTOs — the TypeScript mirror of the durable-workflow-engine
 * run surface on `QuestController` (`durable-workflow-engine` track). These are
 * additive to the existing quest types in `api/client.ts`; nothing here changes
 * the shape of an existing public symbol.
 *
 * Wire contract confirmed against the shipped controllers/models:
 * - `Models/Quest/QuestRunStatus.cs`  → {@link WorkflowRunStatus}
 * - `Models/Quest/QuestRun.cs`        → {@link WorkflowRunResult}
 * - `Models/Quest/QuestExecutionState.cs` + `QuestNodeExecution.cs`
 *                                      → {@link WorkflowExecutionState} / {@link WorkflowNodeExecution}
 * - `Models/Requests/QuestRequests.cs` (`QuestAdvanceRequest` / `QuestSignalRequest`)
 *                                      → {@link AdvanceParams} / {@link SignalParams}
 */

/**
 * Lifecycle position of a single durable {@link WorkflowRunResult}.
 *
 * Mirrors the C# `QuestRunStatus` enum 1:1 (member-for-member). The three
 * non-terminal awaiting states — `Suspended` / `AwaitingSignal` / `AwaitingTimer` —
 * are what the run driver's `.onSuspend(cb)` reacts to; the terminal set is
 * `Succeeded` / `Failed` / `Forked` / `Cancelled` (`QuestRunStatusExtensions.IsTerminal`).
 */
export type WorkflowRunStatus =
  /** Run created; no node has been claimed yet. */
  | "Pending"
  /** At least one node has transitioned out of Pending. */
  | "Running"
  /** All terminal nodes reported Succeeded. Terminal. */
  | "Succeeded"
  /** Any node reported Failed, or supervisor marked the run failed. Terminal. */
  | "Failed"
  /** Parent run that was forked; work continues on a child run. Terminal. */
  | "Forked"
  /** Run was explicitly cancelled before completion. Terminal. */
  | "Cancelled"
  /** Suspended between nodes awaiting an explicit `advance(runId, fromNodeId)`. Non-terminal. */
  | "Suspended"
  /** Parked at a GATE node awaiting `signal(runId, gateId, payload)`. Non-terminal. */
  | "AwaitingSignal"
  /** Parked at a WAIT node until a timer becomes due (fires via the saga trigger). Non-terminal. */
  | "AwaitingTimer";

/** The non-terminal "the run is parked, intervene to resume" states. */
export const AWAITING_STATUSES: readonly WorkflowRunStatus[] = [
  "Suspended",
  "AwaitingSignal",
  "AwaitingTimer",
];

/** The terminal states a run can never transition out of. */
export const TERMINAL_STATUSES: readonly WorkflowRunStatus[] = [
  "Succeeded",
  "Failed",
  "Forked",
  "Cancelled",
];

/** True when the run is parked awaiting an advance / signal / timer. */
export function isAwaiting(status: WorkflowRunStatus): boolean {
  return AWAITING_STATUSES.includes(status);
}

/** True when the run has reached a terminal state (no further transitions). */
export function isTerminal(status: WorkflowRunStatus): boolean {
  return TERMINAL_STATUSES.includes(status);
}

/**
 * One execution attempt of a quest definition — the `GET /api/quest/runs/{runId}`
 * read shape. Mirrors `Models/Quest/QuestRun.cs`.
 */
export interface WorkflowRunResult {
  /** Run identity, unique across all runs of all quests. */
  id: string;
  /** Quest definition this run executes. */
  questId: string;
  /** Avatar that initiated this run. */
  avatarId: string;
  /** Current lifecycle position. */
  status: WorkflowRunStatus;
  /** Wall-clock ISO time the run row was created. */
  startedAt: string;
  /** Wall-clock ISO time the run reached a terminal state; absent while non-terminal. */
  endedAt?: string;
  /** Parent run id if this run was forked from another; absent for root runs. */
  parentRunId?: string;
  /** Node at which the fork occurred (set iff this is a child fork run). */
  forkedAtNodeId?: string;
  /** Free-form audit reason supplied when the fork was triggered. */
  forkReason?: string;
  /** Free-form audit reason when a supervisor explicitly marked the run failed. */
  failReason?: string;
}

/**
 * Per-(run, node) execution record — an element of {@link WorkflowExecutionState}.
 * Mirrors `Models/Quest/QuestNodeExecution.cs`. Note: per-node `state` reuses the
 * existing `QuestNodeState` union from `api/client.ts` (`Pending`/`Running`/…),
 * NOT {@link WorkflowRunStatus} (a run-only concept).
 */
export interface WorkflowNodeExecution {
  /** Execution row identity. */
  id: string;
  /** Owning run. */
  runId: string;
  /** Quest definition node this execution corresponds to. */
  nodeId: string;
  /** Current per-node lifecycle position. */
  state: "Pending" | "Running" | "Succeeded" | "Failed" | "Skipped";
  /** Serialized `AZOAResult<T>` from the handler; absent until Succeeded. */
  output?: string;
  /** Failure message when the node failed. */
  error?: string;
  /** Wall-clock ISO time the row entered Running. */
  startedAt: string;
  /** Wall-clock ISO time the row reached a terminal state; absent while non-terminal. */
  endedAt?: string;
}

/**
 * Richer per-node run projection — the `GET /api/quest/runs/{runId}/execution-state`
 * read shape. Mirrors `Models/Quest/QuestExecutionState.cs`.
 */
export interface WorkflowExecutionState {
  runId: string;
  questId: string;
  status: WorkflowRunStatus;
  startedAt: string;
  endedAt?: string;
  totalNodes: number;
  completedNodes: number;
  failedNodes: number;
  pendingNodes: number;
  nodeExecutions: WorkflowNodeExecution[];
}

/**
 * Body of `POST /api/quest/runs/{runId}/advance` — the `.step(nodeId)` primitive.
 * Mirrors `QuestAdvanceRequest { Guid FromNodeId }`.
 */
export interface AdvanceParams {
  /** The node to resume FROM; the engine advances into its successor(s). A UUID. */
  fromNodeId: string;
}

/**
 * Body of `POST /api/quest/runs/{runId}/signal` — un-parks a gated node.
 * Mirrors `QuestSignalRequest { string GateId; string? Payload }`.
 *
 * NOTE (contract-confirmed relaxation of plan D6): the backend types `GateId`
 * as a plain non-empty STRING (not necessarily a UUID), and `Payload` as a
 * nullable STRING (not an arbitrary object). The driver therefore guards
 * `gateId` as a non-empty string, NOT with `assertUuid`.
 */
export interface SignalParams {
  /** The gate id the parked node is waiting on. A non-empty free string. */
  gateId: string;
  /** Optional signal body carried into the resumed gate node (e.g. "phase-met"). */
  payload?: string | null;
}

/**
 * Parameters for `.start(...)` on a {@link import("./run.js").WorkflowRunHandle}.
 *
 * `actor` is a plain avatar id (NO brand leak — never a tenant/ArdaNova type). When
 * the handle was opened `fromTemplate`, `params` are the `{{param}}` substitution
 * values for instantiation. Amounts inside `params` are always strings.
 */
export interface StartRunParams {
  /**
   * Avatar this run targets. Optional: when `.forActor(childAvatarId)` was set on
   * the handle that is used; otherwise the run executes under the active session
   * token / API-key principal.
   */
  actor?: string;
  /** `{{param}}` substitution values used when starting from a template. */
  params?: Record<string, string>;
}

/**
 * A short-lived child-scoped credential — the `POST /api/tenant/avatars/{id}/credential`
 * response. Mirrors `Models/Requests/TenantRequests.cs` `ChildCredentialResponse`.
 *
 * `token` is the child JWT (subject = the child avatar id) the run driver threads
 * as a per-run `Authorization: Bearer` override; `expiresAt` drives the lazy
 * re-acquisition. NO ArdaNova / tenant concept leaks past this internal shape.
 */
export interface ChildCredentialResult {
  /** The child avatar this credential acts as. */
  avatarId: string;
  /** The short-lived child JWT. Subject is the child avatar id. */
  token: string;
  /** ISO timestamp at which the credential expires. */
  expiresAt: string;
  /** Scopes actually delegated (intersection of requested ∩ tenant's own). */
  scopes: string[];
}

/** Per-call options for value-moving advances (`.step` / `.signal`). */
export interface AdvanceOptions {
  /**
   * Sets the `Idempotency-Key` request header, reusing the same `extraHeaders`
   * plumbing as `executeSwap`. When absent the server falls back to its
   * deterministic content key.
   */
  idempotencyKey?: string;
}
