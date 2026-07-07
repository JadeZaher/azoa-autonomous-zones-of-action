/**
 * Workflow SDK surface — the consumer surface of the workflow-engine initiative.
 *
 * - {@link WorkflowClient} — thin template authoring + run reads/writes over
 *   `AzoaApiClient`.
 * - {@link createQuestFactory} / {@link WorkflowRunHandle} — the fluent `quest()`
 *   run driver (the headline).
 * - {@link nodeConfig} — pure typed builders for the generic node `Config` string.
 */

export { WorkflowClient } from "./client.js";
export {
  WorkflowRunHandle,
  createQuestFactory,
} from "./run.js";
export type { QuestFactory, SuspendCallback } from "./run.js";
export { nodeConfig } from "./node-config.js";
export { assertUuid, assertNonEmptyString } from "./guards.js";
export {
  isAwaiting,
  isTerminal,
  AWAITING_STATUSES,
  TERMINAL_STATUSES,
} from "./types.js";

export type {
  WorkflowRunStatus,
  WorkflowRunResult,
  WorkflowNodeExecution,
  WorkflowExecutionState,
  WorkflowReconciliationResult,
  AdvanceParams,
  SignalParams,
  StartRunParams,
  AdvanceOptions,
  ChildCredentialResult,
} from "./types.js";

export type {
  GateCheckConfig,
  EmitConfig,
  SwapConfig,
  GrantConfig,
  TransferConfig,
  RefundConfig,
  NftMintRequestParams,
  NftTransferRequestParams,
} from "./node-config.js";
