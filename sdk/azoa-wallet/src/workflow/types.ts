/**
 * Workflow run-driver DTOs — the TypeScript mirror of the durable-workflow-engine
 * run surface on `ActionController` (`durable-workflow-engine` track). These are
 * additive to the existing action types in `api/client.ts`; nothing here changes
 * the shape of an existing public symbol.
 *
 * Wire contract confirmed against the shipped controllers/models:
 * - `Models/ActionDefinition/ActionRunStatus.cs`  → {@link WorkflowRunStatus}
 * - `Models/ActionDefinition/ActionRun.cs`        → {@link WorkflowRunResult}
 * - `Models/ActionDefinition/ActionExecutionState.cs` + `ActionNodeExecution.cs`
 *                                      → {@link WorkflowExecutionState} / {@link WorkflowNodeExecution}
 * - `Models/Requests/ActionRequests.cs` (`ActionAdvanceRequest` / `ActionSignalRequest`)
 *                                      → {@link AdvanceParams} / {@link SignalParams}
 */

/**
 * Lifecycle position of a single durable {@link WorkflowRunResult}.
 *
 * Mirrors the C# `ActionRunStatus` enum 1:1 (member-for-member). The four
 * non-terminal awaiting states — `Suspended` / `AwaitingSignal` / `AwaitingTimer` /
 * `AwaitingReconciliation` — are what the run driver's `.onSuspend(cb)` reacts to;
 * the terminal set is `Succeeded` / `Failed` / `Forked` / `Cancelled`
 * (`ActionRunStatusExtensions.IsTerminal`).
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
  | "AwaitingTimer"
  /**
   * Parked after a chain-action node failed with an indeterminate (Pending/Unknown)
   * on-chain confirmation; a reconciliation re-probe (`reconcileRun`) or operator
   * sweep re-checks chain truth and resumes or fails it. NEVER auto-retried.
   * Non-terminal.
   */
  | "AwaitingReconciliation";

/** The non-terminal "the run is parked, intervene to resume" states. */
export const AWAITING_STATUSES: readonly WorkflowRunStatus[] = [
  "Suspended",
  "AwaitingSignal",
  "AwaitingTimer",
  "AwaitingReconciliation",
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
 * One execution attempt of a action definition — the `GET /api/action/runs/{runId}`
 * read shape. Mirrors `Models/ActionDefinition/ActionRun.cs`.
 */
export interface WorkflowRunResult {
  /** Run identity, unique across all runs of all actions. */
  id: string;
  /** ActionDefinition definition this run executes. */
  actionId: string;
  /** User that initiated this run. */
  userId: string;
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
 * Mirrors `Models/ActionDefinition/ActionNodeExecution.cs`. Note: per-node `state` reuses the
 * existing `ActionNodeState` union from `api/client.ts` (`Pending`/`Running`/…),
 * NOT {@link WorkflowRunStatus} (a run-only concept).
 */
export interface WorkflowNodeExecution {
  /** Execution row identity. */
  id: string;
  /** Owning run. */
  runId: string;
  /** ActionDefinition definition node this execution corresponds to. */
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
 * Richer per-node run projection — the `GET /api/action/runs/{runId}/execution-state`
 * read shape. Mirrors `Models/ActionDefinition/ActionExecutionState.cs`.
 */
export interface WorkflowExecutionState {
  runId: string;
  actionId: string;
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
 * Body of `POST /api/action/runs/{runId}/advance` — the `.step(nodeId)` primitive.
 * Mirrors `ActionAdvanceRequest { Guid FromNodeId }`.
 */
export interface AdvanceParams {
  /** The node to resume FROM; the engine advances into its successor(s). A UUID. */
  fromNodeId: string;
}

/**
 * Body of `POST /api/action/runs/{runId}/signal` — un-parks a gated node.
 * Mirrors `ActionSignalRequest { string GateId; string? Payload }`.
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
 * `actor` is a plain user id (NO brand leak — never a tenant/ArdaNova type). When
 * the handle was opened `fromTemplate`, `params` are the `{{param}}` substitution
 * values for instantiation. Amounts inside `params` are always strings.
 */
export interface StartRunParams {
  /**
   * User this run targets. Optional: when `.forActor(childUserId)` was set on
   * the handle that is used; otherwise the run executes under the active session
   * token / API-key principal.
   */
  actor?: string;
  /** `{{param}}` substitution values used when starting from a template. */
  params?: Record<string, string>;
}

/**
 * A short-lived child-scoped credential — the `POST /api/tenant/users/{id}/credential`
 * response. Mirrors `Models/Requests/TenantRequests.cs` `ChildCredentialResponse`.
 *
 * `token` is the child JWT (subject = the child user id) the run driver threads
 * as a per-run `Authorization: Bearer` override; `expiresAt` drives the lazy
 * re-acquisition. NO ArdaNova / tenant concept leaks past this internal shape.
 */
export interface ChildCredentialResult {
  /** The child user this credential acts as. */
  userId: string;
  /** The short-lived child JWT. Subject is the child user id. */
  token: string;
  /** ISO timestamp at which the credential expires. */
  expiresAt: string;
  /** Scopes actually delegated (intersection of requested ∩ tenant's own). */
  scopes: string[];
}

/**
 * Outcome of a reconciliation re-probe over a run parked in
 * `AwaitingReconciliation` — the `POST /api/action/runs/{runId}/reconcile` response.
 * Mirrors `Models/ActionDefinition/ActionReconciliationResult.cs`. NEVER re-broadcasts; a
 * Confirmed tx reconciles to success (no re-mint), a FailedOnChain node is released
 * to retry/compensation, and an indeterminate node stays parked.
 */
export interface WorkflowReconciliationResult {
  /** The run that was re-probed. */
  runId: string;
  /**
   * The run's status AFTER the re-probe — still `AwaitingReconciliation` when at
   * least one node remained indeterminate, else `Running` once the engine resumes.
   */
  status: WorkflowRunStatus;
  /** Nodes whose tx was Confirmed and reconciled to Succeeded (no re-mint). */
  reconciledConfirmed: number;
  /** Nodes whose tx was FailedOnChain and handed back to retry/compensation. */
  releasedFailedOnChain: number;
  /** Nodes still indeterminate (Pending/Unknown) — left parked for the next sweep. */
  stillIndeterminate: number;
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
