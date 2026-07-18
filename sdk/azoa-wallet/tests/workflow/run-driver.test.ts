/**
 * Workflow SDK — run-driver tests (workflow-sdk track, FR-6).
 *
 * Mocks `fetch` (mirroring tests/api/self-audit-one-fix.test.ts:13-24) and proves
 * the fluent `quest()` chain issues the correct ordered HTTP calls and honors
 * every guard. Asserts ordered calls via `mockFetch.mock.calls`.
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { WorkflowClient } from "../../src/workflow/index.js";
import { nodeConfig } from "../../src/workflow/index.js";
import {
  isAwaiting,
  isTerminal,
  AWAITING_STATUSES,
  TERMINAL_STATUSES,
} from "../../src/workflow/index.js";
import type { WorkflowRunStatus } from "../../src/workflow/index.js";
import { API_PATHS } from "../../src/api/api-version.js";
import { isOk } from "../../src/core/result.js";
import { ApiConfigBuilder } from "../builders/index.js";

/**
 * Pinned mirror of C# `Models/Quest/QuestRunStatus.cs` — all 9 members, in enum
 * order. If the C# enum grows, this list AND the union must grow together; the
 * completeness test below fails otherwise.
 */
const ALL_STATUSES: readonly WorkflowRunStatus[] = [
  "Pending",
  "Running",
  "Succeeded",
  "Failed",
  "Forked",
  "Cancelled",
  "Suspended",
  "AwaitingSignal",
  "AwaitingTimer",
  "AwaitingReconciliation",
];

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

const BASE = "http://localhost:5000";

// Stable UUIDs for the fixtures.
const QUEST_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const TEMPLATE_ID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
const RUN_ID = "cccccccc-cccc-cccc-cccc-cccccccccccc";
const NODE_B = "11111111-1111-1111-1111-111111111111";
const NODE_C = "22222222-2222-2222-2222-222222222222";
const CHILD_AVATAR = "dddddddd-dddd-dddd-dddd-dddddddddddd";

/** AZOAResult<T>-wrapped success response (the request() unwrap path). */
function azoaResponse<T>(result: T) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message: "Success", result }),
  });
}

function runResult(overrides: Record<string, unknown> = {}) {
  return {
    id: RUN_ID,
    questId: QUEST_ID,
    avatarId: CHILD_AVATAR,
    status: "Running",
    startedAt: "2026-06-17T00:00:00Z",
    ...overrides,
  };
}

function makeClient() {
  // Tenant principal: X-Api-Key only (no Bearer), so the credential-acquisition
  // call carries X-Api-Key and we can prove the child JWT is layered on later.
  return new AzoaApiClient(
    new ApiConfigBuilder().withBaseUrl(BASE).build() // no token
  );
}

function makeTenantClient() {
  const cfg = new ApiConfigBuilder().withBaseUrl(BASE).build();
  cfg.apiKey = "tenant-api-key";
  return new AzoaApiClient(cfg);
}

beforeEach(() => {
  mockFetch.mockReset();
});

describe("status helpers mirror the engine enum", () => {
  it("isAwaiting / isTerminal classify the run states", () => {
    expect(isAwaiting("Suspended")).toBe(true);
    expect(isAwaiting("AwaitingSignal")).toBe(true);
    expect(isAwaiting("AwaitingTimer")).toBe(true);
    // AwaitingReconciliation is a durable park state — non-terminal, awaiting.
    expect(isAwaiting("AwaitingReconciliation")).toBe(true);
    expect(isAwaiting("Running")).toBe(false);
    expect(isTerminal("Succeeded")).toBe(true);
    expect(isTerminal("Failed")).toBe(true);
    expect(isTerminal("Forked")).toBe(true);
    expect(isTerminal("Cancelled")).toBe(true);
    expect(isTerminal("Suspended")).toBe(false);
    expect(isTerminal("AwaitingReconciliation")).toBe(false);
  });

  it("the union covers all 9 C# QuestRunStatus members with no drift", () => {
    // Every awaiting + terminal + the two active states must partition the union.
    const active: WorkflowRunStatus[] = ["Pending", "Running"];
    const covered = new Set<WorkflowRunStatus>([
      ...active,
      ...AWAITING_STATUSES,
      ...TERMINAL_STATUSES,
    ]);
    expect(covered.size).toBe(ALL_STATUSES.length);
    for (const s of ALL_STATUSES) expect(covered.has(s)).toBe(true);
    // Awaiting and terminal sets are disjoint.
    for (const s of AWAITING_STATUSES) expect(TERMINAL_STATUSES).not.toContain(s);
  });
});

describe("ordered-call: quest(a).start().step(b).step(c)", () => {
  it("issues start-workflow → advance{fromNodeId:b} → advance{fromNodeId:c} in order", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult({ status: "Suspended" }))) // start-workflow
      .mockReturnValueOnce(azoaResponse(runResult({ status: "Suspended" }))) // advance b
      .mockReturnValueOnce(azoaResponse(runResult({ status: "Running" })));  // advance c

    const result = await wf.quest(QUEST_ID).start().step(NODE_B).step(NODE_C);

    expect(isOk(result)).toBe(true);
    expect(mockFetch.mock.calls).toHaveLength(3);

    const [u0, i0] = mockFetch.mock.calls[0];
    expect(u0).toBe(`${BASE}${API_PATHS.QUEST_START_WORKFLOW(QUEST_ID)}`);
    expect(i0.method).toBe("POST");

    const [u1, i1] = mockFetch.mock.calls[1];
    expect(u1).toBe(`${BASE}${API_PATHS.QUEST_RUN_ADVANCE(RUN_ID)}`);
    expect(i1.method).toBe("POST");
    expect(JSON.parse(i1.body).fromNodeId).toBe(NODE_B);

    const [u2, i2] = mockFetch.mock.calls[2];
    expect(u2).toBe(`${BASE}${API_PATHS.QUEST_RUN_ADVANCE(RUN_ID)}`);
    expect(JSON.parse(i2.body).fromNodeId).toBe(NODE_C);
  });

  it("short-circuits the chain on the first error (no further fetch)", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult())) // start ok
      .mockReturnValueOnce(
        Promise.resolve({
          ok: false,
          status: 400,
          json: () => Promise.resolve({ isError: true, message: "boom" }),
        })
      ); // advance b fails — chain must stop here

    const result = await wf.quest(QUEST_ID).start().step(NODE_B).step(NODE_C);

    expect(isOk(result)).toBe(false);
    // start + advance(b) only — advance(c) never issued.
    expect(mockFetch.mock.calls).toHaveLength(2);
  });
});

describe("hybrid: start → signal{gateId, payload}", () => {
  it("issues start-workflow then signal with the gate body", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult({ status: "AwaitingSignal" })))
      .mockReturnValueOnce(azoaResponse(runResult({ status: "Running" })));

    const result = await wf.quest(QUEST_ID).start().signal("gate-phase-1", "phase-met");

    expect(isOk(result)).toBe(true);
    expect(mockFetch.mock.calls).toHaveLength(2);

    const [u1, i1] = mockFetch.mock.calls[1];
    expect(u1).toBe(`${BASE}${API_PATHS.QUEST_RUN_SIGNAL(RUN_ID)}`);
    expect(i1.method).toBe("POST");
    const body = JSON.parse(i1.body);
    expect(body.gateId).toBe("gate-phase-1");
    expect(body.payload).toBe("phase-met");
  });

  it("signal payload defaults to null when omitted", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult()))
      .mockReturnValueOnce(azoaResponse(runResult()));

    await wf.quest(QUEST_ID).start().signal("gate-x");

    const body = JSON.parse(mockFetch.mock.calls[1][1].body);
    expect(body.payload).toBeNull();
  });
});

describe("fromTemplate: instantiate → start-workflow", () => {
  it("instantiates the template then starts the resulting quest", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse({ id: QUEST_ID, name: "q", avatarId: CHILD_AVATAR, status: "Active", nodes: [], edges: [], dependencies: [], metadata: {}, createdDate: "x" })) // instantiate
      .mockReturnValueOnce(azoaResponse(runResult())); // start-workflow

    const result = await wf.quest.fromTemplate(TEMPLATE_ID).start({ params: { amount: "1000" } });

    expect(isOk(result)).toBe(true);
    const [u0] = mockFetch.mock.calls[0];
    expect(u0).toBe(`${BASE}/api/quest/templates/${TEMPLATE_ID}/instantiate`);
    const [u1] = mockFetch.mock.calls[1];
    expect(u1).toBe(`${BASE}${API_PATHS.QUEST_START_WORKFLOW(QUEST_ID)}`);
  });
});

describe("idempotency passthrough", () => {
  it("step(.., {idempotencyKey}) sets the Idempotency-Key header", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult()))
      .mockReturnValueOnce(azoaResponse(runResult()));

    await wf.quest(QUEST_ID).start().step(NODE_B, { idempotencyKey: "key-123" });

    const init = mockFetch.mock.calls[1][1];
    expect(init.headers["Idempotency-Key"]).toBe("key-123");
  });

  it("signal(.., {idempotencyKey}) sets the Idempotency-Key header", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult()))
      .mockReturnValueOnce(azoaResponse(runResult()));

    await wf.quest(QUEST_ID).start().signal("gate", "p", { idempotencyKey: "sig-key" });

    expect(mockFetch.mock.calls[1][1].headers["Idempotency-Key"]).toBe("sig-key");
  });
});

describe("guards throw before any fetch", () => {
  it("quest(badQuestId) throws synchronously", () => {
    const wf = new WorkflowClient(makeClient());
    expect(() => wf.quest("not-a-uuid")).toThrow("Invalid questId");
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it("quest.run(badRunId) throws synchronously", () => {
    const wf = new WorkflowClient(makeClient());
    expect(() => wf.quest.run("nope")).toThrow("Invalid runId");
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it("forActor(badChildId) throws synchronously", () => {
    const wf = new WorkflowClient(makeClient());
    expect(() => wf.quest(QUEST_ID).forActor("bad")).toThrow("Invalid childAvatarId");
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it("bad nodeId short-circuits the chain to an INVALID_INPUT error (no advance fetch)", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(azoaResponse(runResult())); // only start

    const result = await wf.quest(QUEST_ID).start().step("not-a-uuid");

    expect(isOk(result)).toBe(false);
    if (!isOk(result)) expect(result.error.message).toContain("Invalid fromNodeId");
    // start fired; the bad-node advance never reached fetch.
    expect(mockFetch.mock.calls).toHaveLength(1);
  });

  it("empty gateId short-circuits the chain to an INVALID_INPUT error (no signal fetch)", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(azoaResponse(runResult())); // only start

    const result = await wf.quest(QUEST_ID).start().signal("   ");

    expect(isOk(result)).toBe(false);
    if (!isOk(result)) expect(result.error.message).toContain("Invalid gateId");
    expect(mockFetch.mock.calls).toHaveLength(1);
  });
});

describe("child-credential acquisition (forActor)", () => {
  it("issues the credential POST FIRST (tenant X-Api-Key), then uses the child JWT as Bearer on advance", async () => {
    const wf = new WorkflowClient(makeTenantClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult())) // start-workflow (X-Api-Key)
      .mockReturnValueOnce(
        azoaResponse({
          avatarId: CHILD_AVATAR,
          token: "CHILD-JWT",
          expiresAt: "2999-01-01T00:00:00Z",
          scopes: ["quest"],
        })
      ) // credential POST (X-Api-Key)
      .mockReturnValueOnce(azoaResponse(runResult())); // advance (child Bearer)

    const result = await wf
      .quest(QUEST_ID)
      .forActor(CHILD_AVATAR)
      .start()
      .step(NODE_B);

    expect(isOk(result)).toBe(true);
    expect(mockFetch.mock.calls).toHaveLength(3);

    // call 0: start-workflow under tenant X-Api-Key (no Bearer yet)
    const i0 = mockFetch.mock.calls[0][1];
    expect(mockFetch.mock.calls[0][0]).toBe(`${BASE}${API_PATHS.QUEST_START_WORKFLOW(QUEST_ID)}`);
    expect(i0.headers["X-Api-Key"]).toBe("tenant-api-key");
    expect(i0.headers["Authorization"]).toBeUndefined();

    // call 1: credential acquisition under tenant X-Api-Key (the principal)
    const [u1, i1] = mockFetch.mock.calls[1];
    expect(u1).toBe(`${BASE}${API_PATHS.TENANT_CHILD_CREDENTIAL(CHILD_AVATAR)}`);
    expect(i1.method).toBe("POST");
    expect(i1.headers["X-Api-Key"]).toBe("tenant-api-key");
    expect(i1.headers["Authorization"]).toBeUndefined();

    // call 2: advance uses the child JWT as Bearer (override wins for this call)
    const i2 = mockFetch.mock.calls[2][1];
    expect(mockFetch.mock.calls[2][0]).toBe(`${BASE}${API_PATHS.QUEST_RUN_ADVANCE(RUN_ID)}`);
    expect(i2.headers["Authorization"]).toBe("Bearer CHILD-JWT");
  });

  it("acquires the credential once and reuses the cached child JWT across calls", async () => {
    const wf = new WorkflowClient(makeTenantClient());
    mockFetch
      .mockReturnValueOnce(azoaResponse(runResult())) // start
      .mockReturnValueOnce(
        azoaResponse({ avatarId: CHILD_AVATAR, token: "CHILD-JWT", expiresAt: "2999-01-01T00:00:00Z", scopes: [] })
      ) // credential (once)
      .mockReturnValueOnce(azoaResponse(runResult())) // advance b
      .mockReturnValueOnce(azoaResponse(runResult())); // advance c

    await wf.quest(QUEST_ID).forActor(CHILD_AVATAR).start().step(NODE_B).step(NODE_C);

    // start + 1 credential + 2 advances = 4 (NOT 5 — credential not re-fetched)
    expect(mockFetch.mock.calls).toHaveLength(4);
    const credentialCalls = mockFetch.mock.calls.filter(
      ([u]) => u === `${BASE}${API_PATHS.TENANT_CHILD_CREDENTIAL(CHILD_AVATAR)}`
    );
    expect(credentialCalls).toHaveLength(1);
    // both advances carried the same child Bearer
    expect(mockFetch.mock.calls[2][1].headers["Authorization"]).toBe("Bearer CHILD-JWT");
    expect(mockFetch.mock.calls[3][1].headers["Authorization"]).toBe("Bearer CHILD-JWT");
  });
});

describe("status() + onSuspend", () => {
  it("status() maps GET /api/quest/runs/{runId} to a typed WorkflowRunStatus", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(azoaResponse(runResult({ status: "AwaitingTimer" })));

    const handle = wf.quest.run(RUN_ID);
    const res = await handle.status();

    expect(isOk(res)).toBe(true);
    if (isOk(res)) expect(res.value.status).toBe("AwaitingTimer");
    const [u0, i0] = mockFetch.mock.calls[0];
    expect(u0).toBe(`${BASE}${API_PATHS.QUEST_RUN_STATUS(RUN_ID)}`);
    expect(i0.method).toBe("GET");
  });

  it("onSuspend fires when a call leaves the run awaiting", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(azoaResponse(runResult({ status: "Suspended" })));

    const seen: string[] = [];
    await wf
      .quest(QUEST_ID)
      .onSuspend((run) => seen.push(run.status))
      .start();

    expect(seen).toEqual(["Suspended"]);
  });

  it("onSuspend does NOT fire when the run keeps running", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(azoaResponse(runResult({ status: "Running" })));

    const seen: string[] = [];
    await wf.quest(QUEST_ID).onSuspend((run) => seen.push(run.status)).start();

    expect(seen).toEqual([]);
  });
});

describe("nodeConfig builders match the C# config POCO shapes", () => {
  it("gateCheck → { predicate, reads }", () => {
    const out = JSON.parse(nodeConfig.gateCheck({ predicate: "a >= reads.t", reads: { t: "1" } }));
    expect(out).toEqual({ predicate: "a >= reads.t", reads: { t: "1" } });
  });

  it("gateCheck defaults reads to {}", () => {
    expect(JSON.parse(nodeConfig.gateCheck({ predicate: "x" })).reads).toEqual({});
  });

  it("emit → { payload }", () => {
    expect(JSON.parse(nodeConfig.emit({ payload: { event: "granted" } }))).toEqual({
      payload: { event: "granted" },
    });
  });

  it("swap → { request: { chain, quoteId, walletAddress } }", () => {
    const out = JSON.parse(
      nodeConfig.swap({ request: { chain: "algorand", quoteId: "q1", walletAddress: "ADDR" } })
    );
    expect(out).toEqual({ request: { chain: "algorand", quoteId: "q1", walletAddress: "ADDR" } });
  });

  it("grant → { request: NftMintRequest, holonId? }; amounts stay strings", () => {
    const out = JSON.parse(
      nodeConfig.grant({
        request: { walletId: "w1", name: "Reward", chainId: "algorand", metadata: { amount: "1000" } },
        holonId: "h1",
      })
    );
    expect(out.holonId).toBe("h1");
    expect(out.request.walletId).toBe("w1");
    expect(out.request.metadata.amount).toBe("1000");
    expect(typeof out.request.metadata.amount).toBe("string");
  });

  it("transfer / refund → { nftId, request: NftTransferRequest }", () => {
    const t = JSON.parse(nodeConfig.transfer({ nftId: "n1", request: { targetAvatarId: "a1", walletId: "w1" } }));
    expect(t).toEqual({ nftId: "n1", request: { targetAvatarId: "a1", walletId: "w1" } });
    const r = JSON.parse(nodeConfig.refund({ nftId: "n2", request: { targetAvatarId: "a2", walletId: "w2", memo: "m" } }));
    expect(r).toEqual({ nftId: "n2", request: { targetAvatarId: "a2", walletId: "w2", memo: "m" } });
  });

  it("raw passes a string through and serializes an object", () => {
    expect(nodeConfig.raw('{"a":1}')).toBe('{"a":1}');
    expect(nodeConfig.raw({ a: 1 })).toBe('{"a":1}');
  });
});

describe("listRuns", () => {
  it("maps GET /api/quest/{questId}/runs to a typed WorkflowRunResult[]", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(
      azoaResponse([
        runResult({ status: "Succeeded" }),
        runResult({ id: "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", status: "AwaitingReconciliation" }),
      ])
    );

    const res = await wf.listRuns(QUEST_ID);

    expect(isOk(res)).toBe(true);
    if (isOk(res)) {
      expect(res.value).toHaveLength(2);
      expect(res.value[0].status).toBe("Succeeded");
      expect(res.value[1].status).toBe("AwaitingReconciliation");
    }
    const [u0, i0] = mockFetch.mock.calls[0];
    expect(u0).toBe(`${BASE}${API_PATHS.QUEST_RUNS_BY_QUEST(QUEST_ID)}`);
    expect(i0.method).toBe("GET");
  });

  it("assertUuid rejects a bad questId before any fetch", () => {
    const wf = new WorkflowClient(makeClient());
    expect(() => wf.listRuns("not-a-uuid")).toThrow("Invalid questId");
    expect(mockFetch).not.toHaveBeenCalled();
  });
});

describe("reconcileRun", () => {
  it("POSTs /api/quest/runs/{runId}/reconcile and maps the reconciliation result", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(
      azoaResponse({
        runId: RUN_ID,
        status: "Running",
        reconciledConfirmed: 1,
        releasedFailedOnChain: 0,
        stillIndeterminate: 0,
      })
    );

    const res = await wf.reconcileRun(RUN_ID);

    expect(isOk(res)).toBe(true);
    if (isOk(res)) {
      expect(res.value.runId).toBe(RUN_ID);
      expect(res.value.status).toBe("Running");
      expect(res.value.reconciledConfirmed).toBe(1);
      expect(res.value.stillIndeterminate).toBe(0);
    }
    const [u0, i0] = mockFetch.mock.calls[0];
    expect(u0).toBe(`${BASE}${API_PATHS.QUEST_RUN_RECONCILE(RUN_ID)}`);
    expect(i0.method).toBe("POST");
  });

  it("assertUuid rejects a bad runId before any fetch", () => {
    const wf = new WorkflowClient(makeClient());
    expect(() => wf.reconcileRun("nope")).toThrow("Invalid runId");
    expect(mockFetch).not.toHaveBeenCalled();
  });
});

describe("onSuspend fires for AwaitingReconciliation", () => {
  it("treats a run left in AwaitingReconciliation as a park state", async () => {
    const wf = new WorkflowClient(makeClient());
    mockFetch.mockReturnValueOnce(
      azoaResponse(runResult({ status: "AwaitingReconciliation" }))
    );

    const seen: string[] = [];
    await wf.quest(QUEST_ID).onSuspend((run) => seen.push(run.status)).start();

    expect(seen).toEqual(["AwaitingReconciliation"]);
  });
});

describe("facade composition", () => {
  it("WorkflowClient exposes the quest factory with explicit variants", () => {
    const wf = new WorkflowClient(makeClient());
    expect(typeof wf.quest).toBe("function");
    expect(typeof wf.quest.fromTemplate).toBe("function");
    expect(typeof wf.quest.run).toBe("function");
  });
});
