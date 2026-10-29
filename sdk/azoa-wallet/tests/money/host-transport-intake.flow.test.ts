import { afterEach, describe, expect, it, vi } from "vitest";
import {
  MONEY_SCOPES,
  createKeyStore,
  discardCheckout,
  findForbiddenViewKeys,
  memoryStorage,
  readSavedRequest,
  sendCheckout,
  startCashOut,
  startCheckout,
} from "../../src/money/index.js";
import { createNodeEmulator } from "./node-emulator.js";
import type { NodeEmulator } from "./node-emulator.js";
import { RAW_NODE_IDS, rawHostTransport } from "./raw-host-transport.js";

function freshNode(): NodeEmulator {
  const node = createNodeEmulator({ extraFields: RAW_NODE_IDS });
  vi.stubGlobal("fetch", node.fetch);
  return node;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("a host transport that forwards the node's raw objects: the flows narrow at their one intake (LOW-2)", () => {
  it("no attempt carries admissionKey, subject, tenant or provider ids, whether the answer was a view, a final 409's result or a void", async () => {
    const node = freshNode();
    const transport = rawHostTransport(node);
    const keyStore = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    const saved = () => readSavedRequest(keyStore, MONEY_SCOPES.checkout)!;
    const attempts: unknown[] = [];

    // 200 view: the raw projection carries admissionKey plus every id the node merged in.
    const opened = await startCheckout(transport, keyStore, 25_00);
    attempts.push(opened);
    expect(opened.kind).toBe("opened");
    const { intentId } = node.admissions[0]!;

    // The key names its intent, so a resend that meets a final 409 reads the intent back (`checkout.get`).
    node.apply(intentId, "pay", "webhook");
    const finished = await sendCheckout(transport, keyStore, saved());
    attempts.push(finished);
    expect(finished).toMatchObject({ kind: "finished", view: { intentId, status: "complete", settlementState: "payment_received" } });

    // A void on that paid key reveals the raw intent.
    const revealed = await discardCheckout(transport, keyStore, saved());
    attempts.push(revealed);
    expect(revealed).toMatchObject({ kind: "revealed", view: { intentId } });

    // A lost answer on a second key: the resend's 409 INTENT_NEEDS_REVIEW carries the raw claim as `serverResult`.
    const second = createKeyStore({ subject: `${node.userId}-2`, storage: memoryStorage() });
    node.failNextCreate();
    attempts.push(await startCheckout(transport, second, 10_00));
    node.apply(node.admissions[1]!.intentId, "replayWindowLapsed");
    const held = await sendCheckout(transport, second, readSavedRequest(second, MONEY_SCOPES.checkout)!);
    attempts.push(held);
    expect(held).toMatchObject({ kind: "refused", refusal: { code: "needs_review", serverResult: { intentId: node.admissions[1]!.intentId, status: "starting" } } });

    // A cash-out's accepted view, raw from the BFF.
    const cashOut = await startCashOut(transport, keyStore, 10_00);
    attempts.push(cashOut);
    expect(cashOut.kind).toBe("accepted");

    expect(findForbiddenViewKeys(attempts)).toEqual([]);
  });

  it("a refusal code the SDK does not know is narrowed to unconfirmed, so the key is kept and nothing else of the refusal survives", async () => {
    const node = freshNode();
    const raw = rawHostTransport(node);
    const keyStore = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    const transport = {
      ...raw,
      checkout: {
        ...raw.checkout,
        begin: async () => ({ ok: false as const, refusal: { code: "server_error", message: "Upstream timed out.", admissionKey: "u:t:k", retryAfterSeconds: -1 } as never }),
      },
    };

    const attempt = await startCheckout(transport, keyStore, 25_00);

    expect(attempt).toEqual({ kind: "refused", refusal: { code: "unconfirmed", message: "Upstream timed out." }, kept: expect.objectContaining({ amountMinorUnits: 25_00 }) });
    expect(readSavedRequest(keyStore, MONEY_SCOPES.checkout)).not.toBeNull();
  });
});
