/**
 * `WorkflowClient` — the thin template-authoring + run-read surface of the
 * workflow SDK. It wraps the already-present action-template endpoints on
 * `AzoaApiClient` (so DESIGN-once flows are typed and discoverable) and adds the
 * durable-run reads/writes the `action()` driver composes on top.
 *
 * No new HTTP plumbing: every call goes through `AzoaApiClient.request`, reusing
 * its auth / 401-refresh-dedup / AZOAResult-unwrap / verbose-error model. The
 * idempotency key and the per-run child-JWT `Authorization` override both ride the
 * existing `extraHeaders` argument (D2 / D-AUTH).
 */

import type { AzoaApiClient } from "../api/client.js";
import type {
  ActionResult,
  ActionTemplateResult,
  ActionTemplateCreateParams,
} from "../api/client.js";
import { API_PATHS } from "../api/api-version.js";
import type { Result } from "../core/result.js";
import type { SdkError } from "../core/errors.js";
import { assertUuid, assertNonEmptyString } from "./guards.js";
import { createActionFactory } from "./run.js";
import type { ActionFactory } from "./run.js";
import type {
  WorkflowRunResult,
  WorkflowExecutionState,
  WorkflowReconciliationResult,
  AdvanceOptions,
  ChildCredentialResult,
} from "./types.js";

export class WorkflowClient {
  /**
   * The fluent run-driver factory bound to this client's API client. `action(id)`
   * opens a handle on an existing action; `action.fromTemplate(id)` /
   * `action.run(runId)` open the other variants (D3). This is the headline
   * `azoa.workflow.action(...)` entrypoint.
   */
  readonly action: ActionFactory;

  constructor(private readonly api: AzoaApiClient) {
    this.action = createActionFactory(api);
  }

  // ─── Template authoring (DESIGN once) — FR-1 ───

  /**
   * Create a reusable workflow shape as an `ActionTemplate`
   * (`POST /api/action/templates`). Returns the typed `ActionTemplateResult`.
   */
  createTemplate(
    params: ActionTemplateCreateParams
  ): Promise<Result<ActionTemplateResult, SdkError>> {
    return this.api.createActionTemplate(params);
  }

  /** Read a template by id (`GET /api/action/templates/{id}`). `assertUuid`-guarded. */
  getTemplate(templateId: string): Promise<Result<ActionTemplateResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.api.getActionTemplate(templateId);
  }

  /** List all available templates (`GET /api/action/templates`). */
  listTemplates(): Promise<Result<ActionTemplateResult[], SdkError>> {
    return this.api.listActionTemplates();
  }

  /**
   * Instantiate an action from a template with `{{param}}` values
   * (`POST /api/action/templates/{id}/instantiate`). Returns the raw
   * `ActionResult`; the `action()` driver wraps this to flow DESIGN → DRIVE.
   */
  instantiate(
    templateId: string,
    params?: Record<string, string>
  ): Promise<Result<ActionResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.api.instantiateActionTemplate(templateId, params);
  }

  // ─── Durable run reads — FR-2 (.status) ───

  /**
   * Read a run's current lifecycle state (`GET /api/action/runs/{runId}`), typed
   * as {@link WorkflowRunResult} (its `status` is a {@link
   * import("./types.js").WorkflowRunStatus}). `assertUuid`-guarded.
   */
  getRunStatus(runId: string): Promise<Result<WorkflowRunResult, SdkError>> {
    assertUuid(runId, "runId");
    return this.api.request<WorkflowRunResult>(
      "GET",
      API_PATHS.ACTION_RUN_STATUS(runId)
    );
  }

  /**
   * Read the richer per-node run projection
   * (`GET /api/action/runs/{runId}/execution-state`). `assertUuid`-guarded.
   */
  getExecutionState(
    runId: string
  ): Promise<Result<WorkflowExecutionState, SdkError>> {
    assertUuid(runId, "runId");
    return this.api.request<WorkflowExecutionState>(
      "GET",
      API_PATHS.ACTION_RUN_EXECUTION_STATE(runId)
    );
  }

  /**
   * List every run of an action definition (`GET /api/action/{actionId}/runs`),
   * user-scoped, typed as {@link WorkflowRunResult}[] (each `status` is a
   * {@link import("./types.js").WorkflowRunStatus}). `assertUuid`-guarded.
   */
  listRuns(actionId: string): Promise<Result<WorkflowRunResult[], SdkError>> {
    assertUuid(actionId, "actionId");
    return this.api.request<WorkflowRunResult[]>(
      "GET",
      API_PATHS.ACTION_RUNS_BY_ACTION(actionId)
    );
  }

  /**
   * Re-probe a run parked in `AwaitingReconciliation`
   * (`POST /api/action/runs/{runId}/reconcile`, reconcile-before-retry §7). Re-checks
   * chain truth: a Confirmed tx reconciles to success (NO re-mint) and resumes the
   * DAG, a FailedOnChain node is released to retry/compensation, an indeterminate
   * node stays parked. NEVER re-broadcasts. User-scoped; `assertUuid`-guarded.
   */
  reconcileRun(
    runId: string
  ): Promise<Result<WorkflowReconciliationResult, SdkError>> {
    assertUuid(runId, "runId");
    return this.api.request<WorkflowReconciliationResult>(
      "POST",
      API_PATHS.ACTION_RUN_RECONCILE(runId)
    );
  }

  // ─── Durable run writes — the wire calls the run handle composes ───

  /**
   * Start a durable, suspendable run on an existing action
   * (`POST /api/action/{actionId}/start-workflow`). Returns the created
   * `ActionRun` (whose `id` is the runId the handle binds to). `assertUuid`-guarded.
   * Fails (400, message containing "ActionDefinition run conflict") if the action was
   * unpublished/modified concurrently, racing this call's version-confirm
   * (final-hardening F6) — see `isActionConflict` in `api/client.js`.
   */
  startWorkflow(actionId: string): Promise<Result<WorkflowRunResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.api.request<WorkflowRunResult>(
      "POST",
      API_PATHS.ACTION_START_WORKFLOW(actionId)
    );
  }

  /**
   * The `step(nodeId)` primitive: resume a Suspended manual-advance run from
   * `fromNodeId` into its successor (`POST /api/action/runs/{runId}/advance`).
   * `assertUuid` on both ids; optional `idempotencyKey`; optional per-call
   * `Authorization` override (used by `forActor`). Returns the updated run.
   */
  advance(
    runId: string,
    fromNodeId: string,
    options?: AdvanceOptions & { authToken?: string }
  ): Promise<Result<WorkflowRunResult, SdkError>> {
    assertUuid(runId, "runId");
    assertUuid(fromNodeId, "fromNodeId");
    return this.api.request<WorkflowRunResult>(
      "POST",
      API_PATHS.ACTION_RUN_ADVANCE(runId),
      { fromNodeId },
      false,
      buildExtraHeaders(options)
    );
  }

  /**
   * Deliver an external signal to a parked gate node
   * (`POST /api/action/runs/{runId}/signal`). `assertUuid` on `runId`; `gateId`
   * guarded as a non-empty string (contract-confirmed relaxation of D6);
   * `payload` is a string or null. Optional `idempotencyKey` + `authToken`.
   */
  signal(
    runId: string,
    gateId: string,
    payload?: string | null,
    options?: AdvanceOptions & { authToken?: string }
  ): Promise<Result<WorkflowRunResult, SdkError>> {
    assertUuid(runId, "runId");
    assertNonEmptyString(gateId, "gateId");
    return this.api.request<WorkflowRunResult>(
      "POST",
      API_PATHS.ACTION_RUN_SIGNAL(runId),
      { gateId, payload: payload ?? null },
      false,
      buildExtraHeaders(options)
    );
  }

  // ─── Actor abstraction wire call — FR-3 ───

  /**
   * Issue a short-lived child-scoped credential so a tenant principal can act FOR
   * a child user (`POST /api/tenant/users/{childUserId}/credential`). The
   * tenant `X-Api-Key` is the principal for THIS call (no `Authorization`
   * override). `assertUuid`-guarded. `scopes` is optional; when omitted the
   * server issues the full intersection of the tenant's delegable scopes.
   *
   * Mirrors `Models/Requests/TenantRequests.cs` `IssueChildCredentialModel` /
   * `ChildCredentialResponse` ({ userId, token, expiresAt, scopes }).
   */
  issueChildCredential(
    childUserId: string,
    scopes?: string[]
  ): Promise<Result<ChildCredentialResult, SdkError>> {
    assertUuid(childUserId, "childUserId");
    return this.api.request<ChildCredentialResult>(
      "POST",
      API_PATHS.TENANT_CHILD_CREDENTIAL(childUserId),
      scopes && scopes.length > 0 ? { scopes } : undefined
    );
  }
}

/**
 * Merge an optional `Idempotency-Key` and an optional per-call `Authorization:
 * Bearer <childJWT>` override into the `extraHeaders` bag `request` forwards.
 * Returns `undefined` when neither is present so the call is byte-identical to
 * one with no extra headers.
 */
function buildExtraHeaders(
  options?: AdvanceOptions & { authToken?: string }
): Record<string, string> | undefined {
  if (!options) return undefined;
  const headers: Record<string, string> = {};
  if (options.idempotencyKey) headers["Idempotency-Key"] = options.idempotencyKey;
  if (options.authToken) headers["Authorization"] = `Bearer ${options.authToken}`;
  return Object.keys(headers).length > 0 ? headers : undefined;
}
