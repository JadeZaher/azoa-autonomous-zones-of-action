import { afterEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import {
  MONEY_SCOPES,
  acknowledgeCheckout,
  claimRequestKey,
  createDirectMoneyTransport,
  createKeyStore,
  discardCheckout,
  findForbiddenViewKeys,
  isSettledCheckout,
  memoryStorage,
  newRequestKey,
  readSavedRequest,
  rememberServerId,
  sendCashOut,
  sendCheckout,
  startCashOut,
  startCheckout,
} from "../../src/money/index.js";
import type { AzoaMoneyTransport, CheckoutAttempt, CheckoutView, CustodialAccountView, KeyStorage, MoneyResult } from "../../src/money/index.js";
import { runMoneyTransportContract } from "../../src/money/testing/index.js";
import { CASH_OUT_START, CHECKOUT_START, CHECKOUT_VOID, createNodeEmulator } from "./node-emulator.js";
import type { CheckoutEvent, NodeEmulator, NodeEmulatorOptions } from "./node-emulator.js";

/** One page load: a real AzoaApiClient on the person's own session, the direct transport and a key store over the device's storage. */
function pageLoad(node: NodeEmulator, storage: KeyStorage) {
  const client = new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token });
  return { transport: createDirectMoneyTransport(client, node.userId), keyStore: createKeyStore({ subject: node.userId, storage }) };
}

/** A fresh node with the global fetch pointed at it. */
function freshNode(options?: NodeEmulatorOptions): NodeEmulator {
  const node = createNodeEmulator(options);
  vi.stubGlobal("fetch", node.fetch);
  return node;
}

const directTransport = (node: NodeEmulator) => createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
const checkoutPosts = (node: NodeEmulator) => node.requests.filter(request => request.method === "POST");

/** A FIFO Web Locks stand-in shared by every tab of the device: one holder per name; a waiter whose signal aborts is never granted. */
function webLocks() {
  const tails = new Map<string, Promise<void>>();
  return {
    request<T>(name: string, options: { signal?: AbortSignal }, callback: () => Promise<T>): Promise<T> {
      const previous = tails.get(name) ?? Promise.resolve();
      let release!: () => void;
      tails.set(name, new Promise<void>(resolve => (release = resolve)));
      return new Promise<T>((resolve, reject) => {
        let granted = false;
        options.signal?.addEventListener("abort", () => {
          if (!granted) reject(options.signal!.reason);
        }, { once: true });
        void previous.then(() => {
          if (options.signal?.aborted) return release();
          granted = true;
          callback().then(resolve, reject).finally(release);
        });
      });
    },
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("money over the direct transport (P0 flows)", () => {
  it("a lost checkout answer survives a reload and resumes the one admitted payment", async () => {
    const node = freshNode();
    const device = memoryStorage();

    const firstVisit = pageLoad(node, device);
    node.loseNextResponse();
    const lost = await startCheckout(firstVisit.transport, firstVisit.keyStore, 25_00);
    expect(lost).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });
    expect(node.admissions).toHaveLength(1);

    // Reload: a NEW key store instance over the SAME device storage.
    const reload = pageLoad(node, device);
    const saved = readSavedRequest(reload.keyStore, MONEY_SCOPES.checkout);
    expect(saved?.amountMinorUnits).toBe(25_00);
    // The unresolved key blocks a different payment instead of being silently replaced.
    expect(claimRequestKey(reload.keyStore, MONEY_SCOPES.checkout, 10_00).kind).toBe("pending");
    // Another person on the same device sees nothing of it.
    expect(readSavedRequest(createKeyStore({ subject: "someone-else", storage: device }), MONEY_SCOPES.checkout)).toBeNull();

    const resumed = await sendCheckout(reload.transport, reload.keyStore, saved!);
    if (resumed.kind !== "opened") throw new Error(`expected the resumed payment to open, got ${resumed.kind}`);
    expect(node.admissions).toHaveLength(1);
    expect(resumed.view).toMatchObject({ intentId: node.admissions[0]!.intentId, resumed: true });
    expect(resumed.paymentUrl).toBe(node.admissions[0]!.paymentUrl);
    // The key now names its intent, so a later visit resolves it by state; the subject-bearing admission key stayed server-side.
    expect(readSavedRequest(reload.keyStore, MONEY_SCOPES.checkout)?.serverId).toBe(resumed.view.intentId);
    expect(findForbiddenViewKeys(resumed.view)).toEqual([]);
  });

  it("a node 403 consent-revoked body becomes consent_revoked and releases the key", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    node.revokeConsent();

    const attempt = await startCheckout(visit.transport, visit.keyStore, 25_00);

    expect(attempt).toMatchObject({ kind: "refused", refusal: { code: "consent_revoked" }, kept: null });
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)).toBeNull();
    expect(node.admissions).toHaveLength(0);
  });

  it("refuses to build over a client that carries a tenant API key", () => {
    const node = freshNode();
    const tenantClient = new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token, apiKey: "azoa_live_tenant_key" });
    expect(() => createDirectMoneyTransport(tenantClient, node.userId)).toThrow(/apiKey/);
  });
});

describe("same-key 409s for a lost answer (the key names no intent yet, so a final-looking code is put to the node's void-or-reveal)", () => {
  /** A lost answer, then the timeline moves on before the person comes back to resume it. */
  async function resumeLostAnswer(...events: CheckoutEvent[]) {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    node.loseNextResponse();
    await startCheckout(visit.transport, visit.keyStore, 25_00);
    node.apply(node.admissions[0]!.intentId, ...events);
    const saved = readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)!;
    return { node, visit, attempt: await sendCheckout(visit.transport, visit.keyStore, saved) };
  }

  it("INTENT_EXPIRED whose envelope already names the lapsed page's own projection decides by that state directly, releasing the key with no void round trip", async () => {
    const { node, visit, attempt } = await resumeLostAnswer("pageLapsed");

    // The 409's own projection already proves it closed and unpaid, so the flow decides it directly:
    // the node's void-or-reveal is never called (compare the `otherAccount` row below, which has no
    // projection to decide by and still goes to the void).
    expect(node.requests.filter(request => CHECKOUT_VOID.test(request.path))).toHaveLength(0);
    expect(attempt).toMatchObject({ kind: "finished", view: { status: "awaiting_payment", settlementState: "payment_failed", paymentUrl: null } });
    expect(findForbiddenViewKeys(attempt)).toEqual([]);
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)).toBeNull();
    const fresh = await startCheckout(visit.transport, visit.keyStore, 25_00);
    expect(fresh.kind).toBe("opened");
    expect(node.admissions).toHaveLength(2);
  });

  // [situation, timeline after the lost answer, the resend's 409 code, attempt kind, settlement shown, key kept, the void round trip still happens]
  it.each<[string, CheckoutEvent[], string, CheckoutAttempt["kind"], string | null, boolean, boolean]>([
    ["the card was paid on that page, funds not landed yet", ["pay", "webhook"], "INTENT_FINISHED", "finished", "payment_received", true, false],
    ["Stripe expired that page and the webhook closed it", ["expire"], "INTENT_FINISHED", "finished", "payment_failed", false, false],
    ["the admission row belongs to another account", ["otherAccount"], "REQUEST_KEY_IN_USE", "refused", null, true, true],
  ])("%s: %s is decided by its own projection when the 409 carries one, else the node's void decides, and nothing is paid twice", async (_situation, events, code, kind, settlement, kept, calledVoid) => {
    const { node, visit, attempt } = await resumeLostAnswer(...events);
    const requestKey = node.admissions[0]!.requestKey;

    // INTENT_FINISHED's own projection is proof enough to decide by (no void round trip); REQUEST_KEY_IN_USE
    // carries none, so the resend still asks the node to void or reveal the key.
    expect(node.answers.filter(answer => answer.method === "POST").map(answer => answer.code)).toEqual(calledVoid ? [null, code, code] : [null, code]);
    expect(node.requests.filter(request => CHECKOUT_VOID.test(request.path))).toHaveLength(calledVoid ? 1 : 0);
    expect(attempt.kind).toBe(kind);
    expect(findForbiddenViewKeys(attempt)).toEqual([]);
    if (attempt.kind === "finished") expect(attempt.view.settlementState).toBe(settlement);
    if (attempt.kind === "refused") expect(attempt.refusal.code).toBe("unconfirmed");
    // Never released on INTENT_FINISHED unless that projection is settled (the unlanded card payment keeps it).
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)?.requestKey === requestKey).toBe(kept);
    // A paid or undecidable key still blocks a second, different payment; the revealed intent is never voided.
    if (kept) expect((await startCheckout(visit.transport, visit.keyStore, 10_00)).kind).toBe("pending");
    expect(node.isVoided(node.admissions[0]!.intentId)).toBe(false);
    expect(node.admissions).toHaveLength(1);
  });

  it("re-submitting the same amount after a lost answer reuses the key, so it is a resend too: INTENT_FINISHED's own projection decides it", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    node.loseNextResponse();
    await startCheckout(visit.transport, visit.keyStore, 25_00);
    const intentId = node.admissions[0]!.intentId;
    node.apply(intentId, "pay", "webhook");

    const again = await startCheckout(visit.transport, visit.keyStore, 25_00);

    expect(again).toMatchObject({ kind: "finished", view: { intentId, settlementState: "payment_received" } });
    expect(node.requests.filter(request => CHECKOUT_VOID.test(request.path))).toHaveLength(0);
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)?.serverId).toBe(intentId);
    expect(node.admissions).toHaveLength(1);
  });
});

describe("an unattached claim: its first Stripe create failed, so no page ever left the node (B9, B10)", () => {
  /** The first begin claims the row, then Stripe's create fails: the key is kept with no intent id. */
  async function unattachedClaim(options?: NodeEmulatorOptions, how?: "busy" | "inFlight") {
    const node = freshNode(options);
    const visit = pageLoad(node, memoryStorage());
    node.failNextCreate(how);
    const first = await startCheckout(visit.transport, visit.keyStore, 25_00);
    expect(first).toMatchObject({ kind: "refused", refusal: { code: how === "inFlight" ? "unconfirmed" : "rail_busy" }, kept: { amountMinorUnits: 25_00 } });
    const { intentId, requestKey } = node.admissions[0]!;
    const saved = () => readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout);
    const resend = () => sendCheckout(visit.transport, visit.keyStore, saved()!);
    const voids = () => node.requests.filter(request => CHECKOUT_VOID.test(request.path)).length;
    return { node, visit, intentId, requestKey, resend, saved, voids };
  }

  it("Stripe still has the claim-keyed create in flight (409 CONFLICT): the key is kept, and once it settles the resend opens the one page", async () => {
    const { node, visit, intentId, requestKey, resend, saved } = await unattachedClaim({}, "inFlight");

    expect(await resend()).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });
    expect(node.answers.at(-1)).toMatchObject({ status: 409, code: "CONFLICT" });
    expect(saved()?.requestKey).toBe(requestKey);
    expect((await startCheckout(visit.transport, visit.keyStore, 10_00)).kind).toBe("pending");
    node.apply(intentId, "creationSettled");

    expect(await resend()).toMatchObject({ kind: "opened", paymentUrl: node.admissions[0]!.paymentUrl });
    expect(node.admissions).toHaveLength(1);
  });

  it("minutes later the resend meets 409 INTENT_EXPIRED on a `starting` claim: nothing was ever payable, so the key goes with no void round trip and a fresh payment opens (B9, M2a)", async () => {
    const { node, visit, intentId, resend, saved, voids } = await unattachedClaim();
    node.apply(intentId, "creationLapsed");

    const attempt = await resend();

    expect(node.answers.at(-1)).toMatchObject({ status: 409, code: "INTENT_EXPIRED" });
    expect(attempt).toEqual({ kind: "neverStarted", intentId });
    expect(saved()).toBeNull();
    expect(voids()).toBe(0);
    const fresh = await startCheckout(visit.transport, visit.keyStore, 25_00);
    expect(fresh).toMatchObject({ kind: "opened", paymentUrl: node.admissions[1]!.paymentUrl });
    expect(node.admissions).toHaveLength(2);
  });

  it("past the 23-hour replay window the resend is held for review (kept, marked, its projection narrowed), and discarding it is the node's void, whose voided answer releases it (B10, M2b)", async () => {
    const { node, visit, intentId, resend, saved } = await unattachedClaim();
    node.apply(intentId, "replayWindowLapsed");

    const held = await resend();

    expect(held).toMatchObject({ kind: "refused", refusal: { code: "needs_review", serverResult: { intentId, status: "starting", paymentUrl: null } }, kept: { needsReview: true } });
    expect(findForbiddenViewKeys(held)).toEqual([]);
    const discarded = await discardCheckout(visit.transport, visit.keyStore, saved()!);
    expect(discarded).toMatchObject({ kind: "discarded", intentId });
    expect(node.isVoided(intentId)).toBe(true);
    expect(saved()).toBeNull();
    expect((await startCheckout(visit.transport, visit.keyStore, 25_00)).kind).toBe("opened");
    expect(node.admissions).toHaveLength(2);
  });

  it("on node 33168340 that void answers 409 INTENT_NEEDS_REVIEW in the revealed shape: narrowed, and the key stays held", async () => {
    const extraFields = { subjectUserId: "u-1", connectedAccountId: "acct_1" };
    const { node, visit, intentId, resend, saved } = await unattachedClaim({ unreplayableClaimVoid: "needsReview", extraFields });
    node.apply(intentId, "replayWindowLapsed");
    await resend();

    const attempt = await discardCheckout(visit.transport, visit.keyStore, saved()!);

    expect(node.answers.at(-1)).toMatchObject({ status: 409, code: "INTENT_NEEDS_REVIEW", path: expect.stringMatching(CHECKOUT_VOID) });
    expect(attempt).toMatchObject({ kind: "refused", refusal: { code: "needs_review", serverResult: { outcome: "revealed", intent: { intentId, status: "starting" } } } });
    expect(findForbiddenViewKeys(attempt)).toEqual([]);
    expect(saved()).toMatchObject({ requestKey: node.admissions[0]!.requestKey, needsReview: true });
    expect(node.isVoided(intentId)).toBe(false);
  });

  it("quarantined by an operator it is held for review; once the operator reconciles it, the answer naming the intent clears the mark (MEDIUM-3)", async () => {
    const { node, intentId, requestKey, resend, saved } = await unattachedClaim();
    node.apply(intentId, "needsOperator");

    expect(await resend()).toMatchObject({ kind: "refused", refusal: { code: "needs_review" }, kept: { needsReview: true } });
    node.apply(intentId, "operatorResumed");
    const reopened = await resend();

    expect(reopened).toMatchObject({ kind: "opened", view: { intentId, status: "awaiting_payment" } });
    // A later visit must offer it as an ordinary open payment, not "needs a manual check".
    expect(saved()).toEqual({ requestKey, amountMinorUnits: 25_00, serverId: intentId });
  });
});

describe("a key that already names its intent is decided by that intent's state, never by a refusal code (HIGH-1, LOW-1)", () => {
  const railOff = (node: NodeEmulator) => node.disablePayments();
  // [situation, the node's answer to the resend, timeline before the resend, node-wide change, attempt kind, settlement shown, key kept]
  it.each<[string, string, CheckoutEvent[], ((node: NodeEmulator) => void) | null, CheckoutAttempt["kind"], string | null, boolean]>([
    ["card paid, funds not landed yet", "409 INTENT_FINISHED", ["pay", "webhook"], null, "finished", "payment_received", true],
    ["card paid and the funds landed", "409 INTENT_FINISHED", ["pay", "webhook", "applyFunds"], null, "finished", "funds_applied", false],
    ["Stripe expired the open page (provably unpaid)", "409 INTENT_FINISHED", ["expire"], null, "finished", "payment_failed", false],
    ["ACH still clearing past the page's expiry", "200 resumed, processing, no page", ["payAsync", "webhook", "pageLapsed"], null, "opened", "not_started", true],
    // Node LOW-3: a fresh Stripe "complete" outranks a row that recorded "expired" (clients read that as settled-and-unpaid).
    ["the row recorded the page expired, but the payer completed it at Stripe", "409 INTENT_FINISHED, read as processing", ["expire", "pay"], null, "opened", "payment_received", true],
    ["the payment rail was switched off while the page is open", "503 PAYMENTS_NOT_CONFIGURED", [], railOff, "opened", "not_started", true],
    ["the node cannot read Stripe at all", "503 DEPENDENCY_UNAVAILABLE, and on the read", ["stripeDown"], null, "refused", null, true],
  ])("%s: resend answered %s", async (_situation, _answer, events, before, kind, settlement, kept) => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    const opened = await startCheckout(visit.transport, visit.keyStore, 25_00);
    if (opened.kind !== "opened") throw new Error(`expected the first payment to open, got ${opened.kind}`);
    const intentId = opened.view.intentId;
    node.apply(intentId, ...events);
    before?.(node);

    const attempt = await sendCheckout(visit.transport, visit.keyStore, readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)!);

    expect(attempt.kind).toBe(kind);
    expect(findForbiddenViewKeys(attempt)).toEqual([]);
    if (attempt.kind === "opened" || attempt.kind === "finished") expect(attempt.view.settlementState).toBe(settlement);
    // A refused send never hands out a payment page by itself.
    expect(attempt.kind === "opened" ? attempt.paymentUrl : null).toBeNull();
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)?.serverId === intentId).toBe(kept);
    // A kept key still blocks a second, different payment on this device.
    if (kept) expect((await startCheckout(visit.transport, visit.keyStore, 10_00)).kind).toBe("pending");
    expect(node.admissions).toHaveLength(1);
  });
});

describe("the same-key resend's expire-first classifier once a page's clock has lapsed but Stripe still reads open (§stripe-session-verdict)", () => {
  /** Admits a fresh checkout, then lapses its clock while Stripe itself still reports the page `open` (a payer mid-3DS). */
  async function pastExpiryStillOpen(node: NodeEmulator) {
    const transport = directTransport(node);
    const begun = await transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: 25_00 });
    if (!begun.ok) throw new Error("expected the first begin to open");
    const { requestKey, intentId } = node.admissions[0]!;
    node.apply(intentId, "pageLapsedMidConfirmation");
    return { transport, requestKey, intentId };
  }

  it("Stripe still says open past the clock: the node expires it there first, then answers INTENT_EXPIRED whose own projection releases the key", async () => {
    const node = freshNode();
    const { transport, requestKey, intentId } = await pastExpiryStillOpen(node);

    const resend = await transport.checkout.begin({ requestKey, amountMinorUnits: 25_00 });

    expect(resend.ok).toBe(false);
    if (resend.ok) return;
    expect(resend.refusal).toMatchObject({ code: "definite", message: "This payment page has expired. Start a new payment." });
    expect(resend.refusal.serverResult).toMatchObject({ intentId, status: "awaiting_payment", settlementState: "payment_failed", paymentUrl: null });
    expect(findForbiddenViewKeys(resend)).toEqual([]);
    // The clock alone never proves it; the Stripe session itself is what closed, not a void write.
    expect(node.isVoided(intentId)).toBe(false);
  });

  it("the payer finishes mid-3DS exactly as the node's expire call reaches Stripe: the fallback read proves it paid, so the resend resumes it as processing with no page and the key kept", async () => {
    const node = freshNode();
    const { transport, requestKey, intentId } = await pastExpiryStillOpen(node);
    node.apply(intentId, "payDuringExpire");

    const resend = await transport.checkout.begin({ requestKey, amountMinorUnits: 25_00 });

    expect(resend).toMatchObject({
      ok: true,
      value: { intentId, status: "processing", settlementState: "payment_received", paymentUrl: null, resumed: true },
    });
  });

  it("the node cannot settle it at all (Stripe refuses the expire and a fresh read still says open): a retryable refusal keeps the key", async () => {
    const node = freshNode();
    const { transport, requestKey, intentId } = await pastExpiryStillOpen(node);
    node.apply(intentId, "expireFails");

    const resend = await transport.checkout.begin({ requestKey, amountMinorUnits: 25_00 });

    expect(resend).toMatchObject({ ok: false, refusal: { code: "unconfirmed" } });
    expect(node.answers.at(-1)).toMatchObject({ status: 503, code: "DEPENDENCY_UNAVAILABLE" });
    expect(node.isVoided(intentId)).toBe(false);
  });

  it("a finished intent whose Stripe read fails answers 503, never a bodiless 409 (its own projection could not be built)", async () => {
    const node = freshNode();
    const transport = directTransport(node);
    const begun = await transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: 25_00 });
    if (!begun.ok) throw new Error("expected the first begin to open");
    const { requestKey } = node.admissions[0]!;
    node.apply(begun.value.intentId, "pay", "webhook", "stripeDown");

    const resend = await transport.checkout.begin({ requestKey, amountMinorUnits: 25_00 });

    expect(resend).toMatchObject({ ok: false, refusal: { code: "unconfirmed" } });
    expect(node.answers.at(-1)).toMatchObject({ status: 503, code: "DEPENDENCY_UNAVAILABLE" });
  });
});

describe("discarding a key is the node's decision: void-or-reveal by request key", () => {
  /** A saved checkout to discard: one whose answer was lost (no intent id saved), or one whose page opened (intent id saved). */
  async function savedCheckout(node: NodeEmulator, visit: ReturnType<typeof pageLoad>, lostAnswer: boolean) {
    if (lostAnswer) node.loseNextResponse();
    const first = await startCheckout(visit.transport, visit.keyStore, 25_00);
    expect(first.kind).toBe(lostAnswer ? "refused" : "opened");
    const saved = readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)!;
    expect(saved.serverId).toBe(lostAnswer ? undefined : node.admissions[0]!.intentId);
    return saved;
  }

  it.each([["whose answer was lost", true], ["whose page opened", false]])(
    "an open, unpaid page %s: the node expires it at Stripe and voids the key, so the key goes and a new payment opens",
    async (_label, lostAnswer) => {
      const node = freshNode();
      const visit = pageLoad(node, memoryStorage());
      const saved = await savedCheckout(node, visit, lostAnswer);
      const { intentId, requestKey } = node.admissions[0]!;

      const discarded = await discardCheckout(visit.transport, visit.keyStore, saved);

      expect(discarded).toEqual({ kind: "discarded", request: saved, intentId });
      expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)).toBeNull();
      expect(node.isVoided(intentId)).toBe(true);
      // Voiding twice gives the same answer; the voided intent reads closed and unpaid; nothing can ever open under that key.
      expect(await visit.transport.checkout.void(requestKey)).toEqual({ ok: true, value: { outcome: "voided", intentId } });
      const read = await visit.transport.checkout.get(intentId);
      expect(read.ok && read.value.status === "expired" && isSettledCheckout(read.value)).toBe(true);
      expect(await visit.transport.checkout.begin({ requestKey, amountMinorUnits: 25_00 })).toEqual({
        ok: false,
        refusal: { code: "definite", message: "This payment request was cancelled and nothing can be paid under it. Start a new payment." },
      });
      const fresh = await startCheckout(visit.transport, visit.keyStore, 25_00);
      if (fresh.kind !== "opened") throw new Error(`expected a new payment to open, got ${fresh.kind}`);
      expect(fresh.view.intentId).not.toBe(intentId);
      expect(node.admissions).toHaveLength(2);
    },
  );

  it("a begin still on the wire when the person discards is refused REQUEST_VOIDED, so nothing is ever admitted under that key", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    const releaseBegin = node.holdNextRequest(CHECKOUT_START);
    const timedOut = await startCheckout(visit.transport, visit.keyStore, 25_00, { sendTimeoutMs: 30 });
    expect(timedOut).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });

    const discarded = await discardCheckout(visit.transport, visit.keyStore, readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)!);
    releaseBegin();

    // Nothing was admitted, so the node wrote a tombstone; the late begin found it.
    expect(discarded).toMatchObject({ kind: "discarded", intentId: null });
    await vi.waitFor(() => expect(node.answers.filter(answer => CHECKOUT_START.test(answer.path)).map(answer => answer.code)).toEqual(["REQUEST_VOIDED"]));
    expect(node.admissions).toEqual([]);
    expect((await startCheckout(visit.transport, visit.keyStore, 25_00)).kind).toBe("opened");
  });

  it("a void whose answer was lost keeps the key; resuming then meets REQUEST_VOIDED, the void confirms it, and only then the key goes", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    const saved = await savedCheckout(node, visit, true);
    node.loseNextResponse(CHECKOUT_VOID);

    const lost = await discardCheckout(visit.transport, visit.keyStore, saved);

    expect(lost).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" }, kept: { requestKey: saved.requestKey } });
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)?.requestKey).toBe(saved.requestKey);
    const resumed = await sendCheckout(visit.transport, visit.keyStore, saved);
    expect(resumed).toEqual({
      kind: "refused",
      refusal: { code: "definite", message: "This payment request was cancelled and nothing can be paid under it. Start a new payment." },
      kept: null,
    });
    expect(node.requests.filter(request => CHECKOUT_VOID.test(request.path))).toHaveLength(2);
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)).toBeNull();
    expect(node.admissions).toHaveLength(1);
  });

  // [situation, the void's answer, how the node gets there, the refusal the flow keeps the key with]
  it.each<[string, string, (node: NodeEmulator, intentId: string) => void, string]>([
    ["the node cannot read Stripe", "503 DEPENDENCY_UNAVAILABLE", (node, intentId) => node.apply(intentId, "stripeDown"), "unconfirmed"],
    ["Stripe refuses the expire and a fresh read still says open", "503 DEPENDENCY_UNAVAILABLE", (node, intentId) => node.apply(intentId, "expireFails"), "unconfirmed"],
    // `payments_not_configured` releases a begin's key; from a void it proves nothing, so the flow keeps it.
    ["the payment rail was switched off", "503 PAYMENTS_NOT_CONFIGURED", node => node.disablePayments(), "unconfirmed"],
  ])("a void the node cannot decide keeps the key (%s: %s)", async (_situation, answer, arrange, code) => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    const saved = await savedCheckout(node, visit, true);
    arrange(node, node.admissions[0]!.intentId);

    const attempt = await discardCheckout(visit.transport, visit.keyStore, saved);

    const [status, wireCode] = answer.split(" ");
    expect(node.answers.at(-1)).toMatchObject({ status: Number(status), code: wireCode });
    expect(attempt).toMatchObject({ kind: "refused", refusal: { code }, kept: { requestKey: saved.requestKey } });
    expect(findForbiddenViewKeys(attempt)).toEqual([]);
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)?.requestKey).toBe(saved.requestKey);
    expect(node.isVoided(node.admissions[0]!.intentId)).toBe(false);
  });

  it.each<[string, () => Promise<MoneyResult<unknown>>]>([
    ["answers a key-releasing refusal (a BFF that maps a 400 to definite)", async () => ({ ok: false, refusal: { code: "definite", message: "Bad request." } })],
    ["answers an outcome the SDK does not know", async () => ({ ok: true, value: { outcome: "Voided", intentId: null } })],
    ["answers ok with no value", async () => ({ ok: true, value: null })],
    ["rejects", () => Promise.reject(new Error("tRPC exploded"))],
  ])("a host transport whose void %s keeps the key", async (_label, answer) => {
    const node = freshNode();
    const direct = directTransport(node);
    const keyStore = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    const host = { ...direct, checkout: { ...direct.checkout, void: answer } } as unknown as AzoaMoneyTransport;
    node.loseNextResponse();
    await startCheckout(host, keyStore, 25_00);
    const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout)!;

    const attempt = await discardCheckout(host, keyStore, saved);

    expect(attempt).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" }, kept: { requestKey: saved.requestKey } });
    expect(readSavedRequest(keyStore, MONEY_SCOPES.checkout)?.requestKey).toBe(saved.requestKey);
  });
});

describe("a paid intent whose funds never land (no issuance handler, or crediting dead-lettered) is let go only when the person acknowledges it (M3)", () => {
  it("resuming finds it finished and keeps the key, discarding reveals it and keeps the key, and acknowledging releases it without starting anything", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    const opened = await startCheckout(visit.transport, visit.keyStore, 25_00);
    if (opened.kind !== "opened") throw new Error(`expected the first payment to open, got ${opened.kind}`);
    const { intentId } = opened.view;
    node.apply(intentId, "pay", "webhook");
    const saved = () => readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout);

    expect(await sendCheckout(visit.transport, visit.keyStore, saved()!)).toMatchObject({ kind: "finished", view: { status: "complete", settlementState: "payment_received" } });
    const discarded = await discardCheckout(visit.transport, visit.keyStore, saved()!);
    if (discarded.kind !== "revealed") throw new Error(`expected the paid payment to be revealed, got ${discarded.kind}`);
    expect(saved()?.serverId).toBe(intentId);
    const postsBefore = checkoutPosts(node).length;

    expect(acknowledgeCheckout(visit.keyStore, discarded.view)).toBe(true);

    expect(saved()).toBeNull();
    expect(checkoutPosts(node)).toHaveLength(postsBefore);
    expect(node.isVoided(intentId)).toBe(false);
    // The person may now start a separate payment on purpose; the paid one can never be charged again under its key.
    expect((await startCheckout(visit.transport, visit.keyStore, 25_00)).kind).toBe("opened");
    expect(node.admissions).toHaveLength(2);
  });

  // [the watched intent as last read, is it this saved key's intent, released]
  it.each<[string, Partial<CheckoutView>, boolean, boolean]>([
    ["complete + payment_received (paid, funds not landed)", { status: "complete", settlementState: "payment_received" }, true, true],
    ["processing + payment_received (paid at Stripe, not closed yet: keep polling)", { status: "processing", settlementState: "payment_received" }, true, false],
    ["complete + not_started (an older node that could not read Stripe)", { status: "complete", settlementState: "not_started" }, true, false],
    ["awaiting_payment (an open page)", { status: "awaiting_payment", settlementState: "not_started" }, true, false],
    ["complete + payment_received, but another intent than the saved key names", { status: "complete", settlementState: "payment_received" }, false, false],
  ])("acknowledging %s", (_label, state, ownIntent, released) => {
    const keyStore = createKeyStore({ subject: "person-1", storage: memoryStorage() });
    const claimed = claimRequestKey(keyStore, MONEY_SCOPES.checkout, 25_00);
    if (claimed.kind !== "ready") throw new Error("expected a fresh key");
    const intentId = newRequestKey();
    rememberServerId(keyStore, MONEY_SCOPES.checkout, claimed.request.requestKey, intentId);
    const view = { intentId: ownIntent ? intentId : newRequestKey(), paymentUrl: null, resumed: true, amountPaidMinorUnits: 25_00, currency: "usd", expiresAt: null, ...state } as CheckoutView;

    expect(acknowledgeCheckout(keyStore, view)).toBe(released);
    expect(readSavedRequest(keyStore, MONEY_SCOPES.checkout) === null).toBe(released);
  });
});

describe("a hung send cannot freeze the page or other tabs (LOW-2)", () => {
  it("the send times out as unconfirmed with its key kept, a tab waiting on the lock gives up without sending, and the next try resumes the one key", async () => {
    vi.stubGlobal("navigator", { locks: webLocks() });
    const node = freshNode();
    const device = memoryStorage();
    const tabA = pageLoad(node, device);
    const tabB = pageLoad(node, device);
    const hung: AzoaMoneyTransport = { ...tabA.transport, checkout: { ...tabA.transport.checkout, begin: () => new Promise(() => undefined) } };

    const [first, second] = await Promise.all([
      startCheckout(hung, tabA.keyStore, 25_00, { sendTimeoutMs: 80 }),
      startCheckout(tabB.transport, tabB.keyStore, 25_00, { lockTimeoutMs: 20 }),
    ]);

    expect(first).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" }, kept: { amountMinorUnits: 25_00 } });
    expect(second).toMatchObject({ kind: "refused", refusal: { code: "rail_busy" } });
    expect(checkoutPosts(node)).toEqual([]);
    const keptKey = readSavedRequest(tabB.keyStore, MONEY_SCOPES.checkout)?.requestKey;
    expect((await startCheckout(tabB.transport, tabB.keyStore, 25_00)).kind).toBe("opened");
    expect(node.admissions.map(admission => admission.requestKey)).toEqual([keptKey]);
  });
});

describe("resending re-reads storage first (L1)", () => {
  it("a request another tab already resolved is not sent again", async () => {
    const node = freshNode();
    const device = memoryStorage();
    const tabA = pageLoad(node, device);
    const tabB = pageLoad(node, device);
    node.loseNextResponse();
    await startCheckout(tabA.transport, tabA.keyStore, 25_00);
    const stale = readSavedRequest(tabA.keyStore, MONEY_SCOPES.checkout)!;
    // Tab B resumes and the page opens; then the person discards it there and starts another amount.
    await sendCheckout(tabB.transport, tabB.keyStore, stale);
    tabB.keyStore.remove(MONEY_SCOPES.checkout);
    await startCheckout(tabB.transport, tabB.keyStore, 10_00);
    const postsBefore = node.requests.filter(request => request.method === "POST").length;

    const attempt = await sendCheckout(tabA.transport, tabA.keyStore, stale);

    expect(attempt).toEqual({ kind: "superseded", current: readSavedRequest(tabA.keyStore, MONEY_SCOPES.checkout) });
    expect(node.requests.filter(request => request.method === "POST")).toHaveLength(postsBefore);
  });
});

describe("cash-out answers (L2, L4)", () => {
  it("a same-key replay of a refused cash-out (the node answers 200 with the refused row) is a refusal, not a success", async () => {
    const node = freshNode({ balanceMinorUnits: 50_00 });
    const visit = pageLoad(node, memoryStorage());
    // The node claims the row, refuses it durably, and that refusal's answer is lost.
    node.loseNextResponse(CASH_OUT_START);
    const lost = await startCashOut(visit.transport, visit.keyStore, 80_00);
    expect(lost).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });
    expect(node.cashOuts[0]?.status).toBe("Refused");

    const replay = await sendCashOut(visit.transport, visit.keyStore, readSavedRequest(visit.keyStore, MONEY_SCOPES.cashOut)!);

    expect(replay).toEqual({
      kind: "refused",
      refusal: { code: "definite", message: "That is more than you have available to cash out." },
      kept: null,
    });
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.cashOut)).toBeNull();
  });

  it.each([
    ["rejects", () => Promise.reject(new Error("tRPC exploded"))],
    ["resolves ok with no view", () => Promise.resolve({ ok: true, value: null })],
    ["resolves junk", () => Promise.resolve("oops")],
  ])("a host transport that %s is unconfirmed and the key survives", async (_label, start) => {
    const node = freshNode();
    const direct = directTransport(node);
    const keyStore = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    const broken = { ...direct, cashOut: { ...direct.cashOut, start } } as unknown as AzoaMoneyTransport;

    const attempt = await startCashOut(broken, keyStore, 10_00);

    expect(attempt).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });
    expect(attempt.kind === "refused" && attempt.kept?.amountMinorUnits).toBe(10_00);
    expect(readSavedRequest(keyStore, MONEY_SCOPES.cashOut)?.amountMinorUnits).toBe(10_00);
  });
});

describe("two tabs share one device (L8)", () => {
  /**
   * Cross-tab `localStorage` is not atomic: each tab sees its own writes at once and the other tab's a few
   * milliseconds later. This models that, so two tabs that claim in the same instant both see an empty slot.
   */
  function sharedDevice() {
    const shared = new Map<string, string>();
    const tab = (): KeyStorage => {
      const own = new Map<string, string | null>();
      const propagate = (key: string, value: string | null) => {
        own.set(key, value);
        setTimeout(() => {
          if (value === null) shared.delete(key);
          else shared.set(key, value);
          own.delete(key);
        }, 5);
      };
      return {
        getItem: key => (own.has(key) ? own.get(key)! : shared.get(key) ?? null),
        setItem: (key, value) => propagate(key, value),
        removeItem: key => propagate(key, null),
      };
    };
    return { tab };
  }

  async function bothTabsPay() {
    const node = freshNode({ latencyMs: 20 });
    const device = sharedDevice();
    const tabA = pageLoad(node, device.tab());
    const tabB = pageLoad(node, device.tab());
    const answers = await Promise.all([startCheckout(tabA.transport, tabA.keyStore, 25_00), startCheckout(tabB.transport, tabB.keyStore, 25_00)]);
    return { node, answers };
  }

  it("with Web Locks, two tabs paying the same amount at once open one payment", async () => {
    vi.stubGlobal("navigator", { locks: webLocks() });

    const { node, answers } = await bothTabsPay();

    expect(node.admissions).toHaveLength(1);
    expect(answers.map(answer => answer.kind)).toEqual(["opened", "opened"]);
  });

  it("control: without Web Locks the same race mints two keys (the hazard the lock removes)", async () => {
    vi.stubGlobal("navigator", {});

    const { node } = await bothTabsPay();

    expect(node.admissions).toHaveLength(2);
  });
});

describe("browser-safe views (L7)", () => {
  // A node that later adds identifying fields to every body must not leak them through any view.
  const future = { subjectUserId: "u-1", connectedAccountId: "acct_1", stripeAccountId: "acct_2", internalNote: "x" };

  it("every direct-transport view is allowlist-narrowed, mutations included", async () => {
    const node = freshNode({ extraFields: future });
    const transport = directTransport(node);

    const begun = await transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: 25_00 });
    const views = {
      begin: begun,
      capabilities: await transport.checkout.capabilities(),
      kyc: await transport.account.beginKyc(),
      quote: await transport.cashOut.availability(),
      destination: await transport.cashOut.destination(),
      setup: await transport.cashOut.beginDestinationSetup(),
      start: await transport.cashOut.start({ requestKey: newRequestKey(), amountMinorUnits: 10_00 }),
      history: await transport.cashOut.history(),
    };

    const keys = (result: { ok: boolean; value?: unknown }) => (result.ok ? Object.keys(Array.isArray(result.value) ? result.value[0] : result.value as object).sort() : null);
    expect(keys(views.begin)).toEqual(["amountPaidMinorUnits", "currency", "expiresAt", "intentId", "paymentUrl", "resumed", "settlementState", "status"]);
    expect(keys(views.capabilities)).toEqual(["available", "unavailableReason"]);
    expect(keys(views.kyc)).toEqual(["developmentSimulation", "expiresAt", "instructions", "verificationUrl"]);
    expect(keys(views.quote)).toEqual([
      "accountReady", "available", "availableFrom", "cashableMinorUnits", "currencyCode", "maximumAmountMinorUnits", "minimumAmountMinorUnits",
      "onHoldMinorUnits", "statusMessage",
    ]);
    const destinationKeys = ["accountOpened", "available", "detailsSubmitted", "outstandingRequirementCount", "readyToReceive", "setupUrl", "setupUrlExpiresAt", "statusMessage"];
    expect(keys(views.destination)).toEqual(destinationKeys);
    expect(keys(views.setup)).toEqual(destinationKeys);
    const cashOutKeys = ["amountMinorUnits", "currencyCode", "id", "paidAt", "requestedAt", "reversedAt", "status", "statusMessage"];
    expect(keys(views.start)).toEqual(cashOutKeys);
    expect(keys(views.history)).toEqual(cashOutKeys);
  });

  it("passes the reusable transport contract with every case reached, against a node that adds fields", async () => {
    const report = await runMoneyTransportContract(() => {
      const node = freshNode({ extraFields: future });
      return {
        transport: directTransport(node),
        checkout: { markPaid: intentId => node.apply(intentId, "pay"), makeUnreadable: intentId => node.apply(intentId, "stripeDown") },
      };
    }, { requireAll: true });

    expect(report.failed).toEqual([]);
    expect(report.skipped).toEqual([]);
    expect(report.passed).toHaveLength(11);
  });

  it("a transport without fixture controls skips the void cases that need them, and requireAll counts that as not ok", async () => {
    const report = await runMoneyTransportContract(() => directTransport(freshNode()), { requireAll: true });

    expect(report.failed).toEqual([]);
    expect(report.skipped.map(skip => skip.name)).toEqual([
      "checkout.void on a paid checkout reveals it, narrowed, and never voids it",
      "checkout.void that cannot read the provider refuses with a code that keeps the key",
    ]);
    expect(report.ok).toBe(false);
  });

  it("the contract fails a transport that opens a second checkout per key and leaks the admission key", async () => {
    const broken = (): AzoaMoneyTransport => {
      const direct = directTransport(freshNode());
      return {
        ...direct,
        checkout: {
          ...direct.checkout,
          begin: async request => {
            const result = await direct.checkout.begin({ ...request, requestKey: newRequestKey() });
            return result.ok ? { ok: true, value: { ...result.value, admissionKey: "subject:tenant:key" } as CheckoutView } : result;
          },
        },
      };
    };

    const report = await runMoneyTransportContract(broken);

    expect(report.ok).toBe(false);
    expect(report.failed.map(failure => failure.name)).toEqual(expect.arrayContaining([
      "no view carries subject or tenant identifiers",
      "checkout.begin resumes the same intent for the same request key",
    ]));
  });

  it("the contract fails a transport whose void always says voided (a client-side discard dressed up as the node's)", async () => {
    const report = await runMoneyTransportContract(() => {
      const node = freshNode();
      const direct = directTransport(node);
      return {
        transport: { ...direct, checkout: { ...direct.checkout, void: async () => ({ ok: true, value: { outcome: "voided", intentId: null } }) } },
        checkout: { markPaid: intentId => node.apply(intentId, "pay"), makeUnreadable: intentId => node.apply(intentId, "stripeDown") },
      };
    });

    expect(report.ok).toBe(false);
    expect(report.failed.map(failure => failure.name)).toEqual(expect.arrayContaining([
      "checkout.void on a paid checkout reveals it, narrowed, and never voids it",
      "checkout.void that cannot read the provider refuses with a code that keeps the key",
    ]));
  });

  it.each([
    ["account.ensure", (direct: AzoaMoneyTransport): AzoaMoneyTransport => ({
      ...direct,
      account: {
        ...direct.account,
        ensure: async () => {
          const result = await direct.account.ensure();
          return result.ok ? { ok: true, value: { ...result.value, userId: "u-1" } as CustodialAccountView } : result;
        },
      },
    })],
    ["cashOut.start", (direct: AzoaMoneyTransport): AzoaMoneyTransport => ({
      ...direct,
      cashOut: {
        ...direct.cashOut,
        start: async request => {
          const result = await direct.cashOut.start(request);
          return result.ok ? { ok: true, value: { ...result.value, connectedAccountId: "acct_1" } } : result;
        },
      },
    })],
    // A refusal for an unknown intent cannot leak, so only a read of the intent `begin` just opened catches this one.
    ["checkout.get(", (direct: AzoaMoneyTransport): AzoaMoneyTransport => ({
      ...direct,
      checkout: {
        ...direct.checkout,
        get: async intentId => {
          const result = await direct.checkout.get(intentId);
          return result.ok ? { ok: true, value: { ...result.value, admissionKey: "subject:tenant:key" } as CheckoutView } : result;
        },
      },
    })],
  ])("the contract checks %s for leaked identifiers too", async (label, leak) => {
    const report = await runMoneyTransportContract(() => leak(directTransport(freshNode())));

    const failure = report.failed.find(entry => entry.name === "no view carries subject or tenant identifiers");
    expect(failure?.reason).toContain(label);
  });

  it("the contract key-checks a paid key's same-key resend, so a BFF that passes a final 409's raw result through fails it", async () => {
    const report = await runMoneyTransportContract(() => {
      const node = freshNode();
      const direct = directTransport(node);
      const intentByKey = new Map<string, string>();
      return {
        transport: {
          ...direct,
          checkout: {
            ...direct.checkout,
            begin: async request => {
              const known = intentByKey.get(request.requestKey);
              if (known === undefined) {
                const first = await direct.checkout.begin(request);
                if (first.ok) intentByKey.set(request.requestKey, first.value.intentId);
                return first;
              }
              const raw = { intentId: known, admissionKey: `${node.userId}:self:${request.requestKey}`, status: "complete", settlementState: "payment_received" };
              return { ok: false, refusal: { code: "definite", message: "This payment is already finished.", serverResult: raw as unknown as CheckoutView } };
            },
          },
        },
        checkout: { markPaid: intentId => node.apply(intentId, "pay"), makeUnreadable: intentId => node.apply(intentId, "stripeDown") },
      };
    });

    const failure = report.failed.find(entry => entry.name === "no view carries subject or tenant identifiers");
    expect(failure?.reason).toBe("checkout.begin (same key, after markPaid) leaked serverResult.admissionKey");
  });
});
