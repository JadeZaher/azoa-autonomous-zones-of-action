/**
 * The fluent `quest()` run driver — the headline of the workflow SDK.
 *
 * `quest(questId)` / `quest.fromTemplate(templateId)` / `quest.run(runId)` open a
 * {@link WorkflowRunHandle} (D3 — explicit variants, no id-shape sniffing). The
 * handle is a **thenable** (D7): each chained method (`.start` / `.step` /
 * `.signal`) enqueues an async op and returns `this`, so
 *
 * ```ts
 * const r = await quest(questId).start({ params }).step(nodeB).step(nodeC);
 * ```
 *
 * issues `start-workflow → advance{fromNodeId:nodeB} → advance{fromNodeId:nodeC}`
 * IN ORDER and resolves to the final `Result<WorkflowRunResult, SdkError>`. The
 * FIRST `err` short-circuits the rest of the chain (no throw — Result discipline);
 * the terminal `then` resolves to that error.
 *
 * `.forActor(childAvatarId)` makes a tenant principal act FOR a child avatar: on
 * the first advancement call the handle lazily acquires a child credential
 * (tenant `X-Api-Key` is the principal for that call), caches the child JWT for
 * the handle's lifetime, re-acquires it on expiry (deduped like the API client's
 * `_refreshInFlight`), and threads it as a per-run `Authorization: Bearer`
 * override on `advance` / `signal` — leaving the global tenant `X-Api-Key`
 * untouched (D-AUTH). No `.forActor()` ⇒ the run uses the active session token.
 */

import type { Result } from "../core/result.js";
import { ok, err, isOk } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import { assertUuid } from "./guards.js";
import { WorkflowClient } from "./client.js";
import type { OasisApiClient } from "../api/client.js";
import {
  isAwaiting,
  type WorkflowRunResult,
  type WorkflowRunStatus,
  type StartRunParams,
  type AdvanceOptions,
} from "./types.js";

/** How the handle was opened — disambiguates `.start()` behavior (D3). */
type HandleSource =
  | { kind: "quest"; questId: string }
  | { kind: "template"; templateId: string }
  | { kind: "run"; runId: string };

/** A callback fired when a call leaves the run in an awaiting/suspended state. */
export type SuspendCallback = (run: WorkflowRunResult) => void;

/**
 * A bound, chainable, awaitable durable-run handle. Construct via the {@link quest}
 * factory; do not instantiate directly.
 */
export class WorkflowRunHandle implements PromiseLike<Result<WorkflowRunResult, SdkError>> {
  private readonly workflow: WorkflowClient;

  /** The concrete run id once `.start()` (or `quest.run`) has bound it. */
  private _runId?: string;
  /** The most recent run projection returned by any advancement / status call. */
  private _lastRun?: WorkflowRunResult;
  /** Sticky terminal error: once set, every queued op short-circuits to it. */
  private _error?: SdkError;

  /** The child avatar this handle acts FOR (lazy credential), if `.forActor` set. */
  private _actorAvatarId?: string;
  /** Cached child JWT + its expiry, per the handle's lifetime. */
  private _childToken?: { token: string; expiresAt: number };
  /** Deduped in-flight credential acquisition (mirrors `_refreshInFlight`). */
  private _credentialInFlight?: Promise<Result<string, SdkError>>;

  private readonly _onSuspend: SuspendCallback[] = [];

  /**
   * The serialized op queue. Every chained method appends to this promise so ops
   * run strictly in order; `then` awaits the tail. Seeded resolved.
   */
  private _queue: Promise<void> = Promise.resolve();

  constructor(
    api: OasisApiClient,
    private readonly source: HandleSource
  ) {
    this.workflow = new WorkflowClient(api);
    if (source.kind === "run") this._runId = source.runId;
  }

  // ─── Configuration ───

  /**
   * Act FOR a child avatar: a tenant principal (authed by `X-Api-Key`) lazily
   * acquires the child's short-lived credential and uses it as the Bearer token
   * for this run's advancement calls. Takes a plain avatar id — NO brand leak.
   */
  forActor(childAvatarId: string): this {
    assertUuid(childAvatarId, "childAvatarId");
    this._actorAvatarId = childAvatarId;
    return this;
  }

  /** Register a callback fired when a call leaves the run awaiting/suspended. */
  onSuspend(cb: SuspendCallback): this {
    this._onSuspend.push(cb);
    return this;
  }

  // ─── Chainable advancement ops (DRIVE) ───

  /**
   * Start a durable run. From a template: instantiate with `{{params}}` first,
   * then `start-workflow` on the resulting quest. From a quest: `start-workflow`
   * directly. From a run: a no-op bind (already started). Binds the handle to the
   * returned `runId`. Chainable.
   *
   * `params.actor`, when present, is treated like `.forActor(actor)` (a plain
   * avatar id) so `start({ actor })` and `.forActor(id)` are interchangeable.
   */
  start(params?: StartRunParams): this {
    return this._enqueue(async () => {
      if (params?.actor) {
        assertUuid(params.actor, "actor");
        this._actorAvatarId = params.actor;
      }

      let questId: string;
      if (this.source.kind === "template") {
        const inst = await this.workflow.instantiate(
          this.source.templateId,
          params?.params
        );
        if (!isOk(inst)) return err(inst.error);
        questId = inst.value.id;
      } else if (this.source.kind === "quest") {
        questId = this.source.questId;
      } else {
        // Already a concrete run — nothing to start; just read current state.
        return this.workflow.getRunStatus(this.source.runId);
      }

      const started = await this.workflow.startWorkflow(questId);
      if (!isOk(started)) return err(started.error);
      this._runId = started.value.id;
      return ok(started.value);
    });
  }

  /**
   * The `step(nodeId)` primitive: `POST runs/{runId}/advance {fromNodeId:nodeId}`.
   * `assertUuid(nodeId)`. Optional `idempotencyKey`. Chainable.
   */
  step(nodeId: string, options?: AdvanceOptions): this {
    return this._enqueue(async () => {
      assertUuid(nodeId, "fromNodeId");
      const runId = this._requireRunId();
      const authToken = await this._resolveAuthToken();
      if (!isOk(authToken)) return err(authToken.error);
      return this.workflow.advance(runId, nodeId, {
        idempotencyKey: options?.idempotencyKey,
        authToken: authToken.value,
      });
    });
  }

  /**
   * Un-park a gated node: `POST runs/{runId}/signal {gateId, payload}`. `gateId`
   * is guarded as a non-empty string (contract-confirmed relaxation of D6);
   * `payload` is a string or null. Optional `idempotencyKey`. Chainable.
   */
  signal(
    gateId: string,
    payload?: string | null,
    options?: AdvanceOptions
  ): this {
    return this._enqueue(async () => {
      const runId = this._requireRunId();
      const authToken = await this._resolveAuthToken();
      if (!isOk(authToken)) return err(authToken.error);
      return this.workflow.signal(runId, gateId, payload, {
        idempotencyKey: options?.idempotencyKey,
        authToken: authToken.value,
      });
    });
  }

  // ─── Reads + accessors ───

  /**
   * Explicitly poll the run's current state (`GET /api/quest/runs/{runId}`),
   * mapped to a typed run projection. Awaits any queued ops first so it reflects
   * the post-chain state. Returns `Result<WorkflowRunResult, SdkError>`.
   */
  async status(): Promise<Result<WorkflowRunResult, SdkError>> {
    await this._queue;
    if (this._error) return err(this._error);
    const runId = this._runId;
    if (!runId) {
      return err(
        new SdkError(
          SdkErrorCode.INVALID_INPUT,
          "status(): run is not started yet — call .start() (or open with quest.run(runId)) first"
        )
      );
    }
    const res = await this.workflow.getRunStatus(runId);
    if (isOk(res)) {
      this._lastRun = res.value;
      this._fireSuspendIfAwaiting(res.value);
    }
    return res;
  }

  /** The concrete run id, once bound by `.start()` / `quest.run(runId)`. */
  get runId(): string | undefined {
    return this._runId;
  }

  /** The most recent run lifecycle status observed, if any. */
  get lastStatus(): WorkflowRunStatus | undefined {
    return this._lastRun?.status;
  }

  /** The most recent full run projection observed, if any. */
  get lastRun(): WorkflowRunResult | undefined {
    return this._lastRun;
  }

  // ─── Thenable (D7) ───

  /**
   * Resolve the queued chain to the final `Result<WorkflowRunResult, SdkError>`.
   * Never rejects: a failed op resolves to its `err` (Result discipline). This is
   * what makes `await quest(a).start(...).step(b)` work.
   */
  then<TResult1 = Result<WorkflowRunResult, SdkError>, TResult2 = never>(
    onfulfilled?:
      | ((value: Result<WorkflowRunResult, SdkError>) => TResult1 | PromiseLike<TResult1>)
      | null,
    onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
  ): PromiseLike<TResult1 | TResult2> {
    const settled = this._queue.then<Result<WorkflowRunResult, SdkError>>(() => {
      if (this._error) return err(this._error);
      if (this._lastRun) return ok(this._lastRun);
      // No advancement op ran (e.g. bare `await quest.run(id)`); surface the
      // input error rather than a phantom success.
      return err(
        new SdkError(
          SdkErrorCode.INVALID_INPUT,
          "no run state available — the chain issued no advancement call"
        )
      );
    });
    return settled.then(onfulfilled, onrejected);
  }

  // ─── Internals ───

  /**
   * Append an op to the serialized queue. Once a sticky error is set every later
   * op short-circuits (the chain aborts). The op returns a `Result`; its `value`
   * becomes the new `_lastRun`, its `error` the sticky `_error`.
   */
  private _enqueue(
    op: () => Promise<Result<WorkflowRunResult, SdkError>>
  ): this {
    this._queue = this._queue.then(async () => {
      if (this._error) return; // already short-circuited
      try {
        const res = await op();
        if (isOk(res)) {
          this._lastRun = res.value;
          this._fireSuspendIfAwaiting(res.value);
        } else {
          this._error = res.error;
        }
      } catch (e) {
        // The only throw path here is a synchronous input guard (assertUuid /
        // non-empty-string). Convert it to the sticky Result error so the chain
        // short-circuits without rejecting the thenable.
        this._error =
          e instanceof SdkError
            ? e
            : new SdkError(SdkErrorCode.UNKNOWN, String(e), {
                cause: e as Error,
              });
      }
    });
    return this;
  }

  private _requireRunId(): string {
    if (!this._runId) {
      throw new SdkError(
        SdkErrorCode.INVALID_INPUT,
        "advancement called before the run was started — call .start() (or open with quest.run(runId)) first"
      );
    }
    return this._runId;
  }

  private _fireSuspendIfAwaiting(run: WorkflowRunResult): void {
    if (isAwaiting(run.status)) {
      for (const cb of this._onSuspend) cb(run);
    }
  }

  /**
   * Resolve the `Authorization: Bearer` token to thread on the NEXT advancement
   * call. When no actor is set, returns `ok(undefined)` so the API client's
   * existing auth (session JWT or `X-Api-Key`) is used unchanged. When an actor
   * IS set, lazily acquires + caches the child credential, re-acquiring on expiry
   * with a deduped in-flight promise (mirrors the API client's `_refreshInFlight`).
   */
  private async _resolveAuthToken(): Promise<Result<string | undefined, SdkError>> {
    if (!this._actorAvatarId) return ok(undefined);

    const now = Date.now();
    // Re-acquire ~30s before the cached token's stated expiry to avoid using a
    // token that expires mid-flight.
    const SKEW_MS = 30_000;
    if (this._childToken && this._childToken.expiresAt - SKEW_MS > now) {
      return ok(this._childToken.token);
    }

    if (!this._credentialInFlight) {
      const avatarId = this._actorAvatarId;
      this._credentialInFlight = (async () => {
        const res = await this.workflow.issueChildCredential(avatarId);
        if (!isOk(res)) return err(res.error);
        const expiresAt = Date.parse(res.value.expiresAt);
        this._childToken = {
          token: res.value.token,
          expiresAt: Number.isNaN(expiresAt) ? now + 60_000 : expiresAt,
        };
        return ok(res.value.token);
      })().finally(() => {
        this._credentialInFlight = undefined;
      });
    }
    return this._credentialInFlight;
  }
}

/**
 * The fluent run-driver factory. `quest(questId)` opens a handle bound to an
 * existing quest; use the explicit variants to open from a template or a prior
 * run (D3 — no id-shape sniffing).
 *
 * The factory is created per-`OasisApiClient` by {@link createQuestFactory}; the
 * facade exposes the bound instance as `oasis.workflow.quest`.
 */
export interface QuestFactory {
  /** Open a handle bound to an existing quest id (`assertUuid`-guarded). */
  (questId: string): WorkflowRunHandle;
  /** Open a handle that will instantiate + start from a template (`assertUuid`-guarded). */
  fromTemplate(templateId: string): WorkflowRunHandle;
  /** Open a handle bound to a prior run id, to drive it further (`assertUuid`-guarded). */
  run(runId: string): WorkflowRunHandle;
}

/** Build a {@link QuestFactory} bound to one API client. */
export function createQuestFactory(api: OasisApiClient): QuestFactory {
  const factory = ((questId: string): WorkflowRunHandle => {
    assertUuid(questId, "questId");
    return new WorkflowRunHandle(api, { kind: "quest", questId });
  }) as QuestFactory;

  factory.fromTemplate = (templateId: string): WorkflowRunHandle => {
    assertUuid(templateId, "templateId");
    return new WorkflowRunHandle(api, { kind: "template", templateId });
  };

  factory.run = (runId: string): WorkflowRunHandle => {
    assertUuid(runId, "runId");
    return new WorkflowRunHandle(api, { kind: "run", runId });
  };

  return factory;
}
