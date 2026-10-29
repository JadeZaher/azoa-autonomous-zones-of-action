// @vitest-environment jsdom
import { StrictMode } from "react";
import type { ReactNode } from "react";
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import {
  MONEY_SCOPES,
  claimRequestKey,
  createDirectMoneyTransport,
  createKeyStore,
  defaultMoneyMessages,
  findForbiddenViewKeys,
  newRequestKey,
  readSavedRequest,
} from "../../src/money/index.js";
import type { AzoaMoneyTransport, CheckoutView, MoneyRedirectKind, MoneyRefusal } from "../../src/money/index.js";
import { AddFundsForm, AzoaMoneyProvider, useAddFunds } from "../../src/react/index.js";
import type { AddFundsFormProps, AzoaMoneyProviderProps } from "../../src/react/index.js";
import { CHECKOUT_VOID, createNodeEmulator } from "../money/node-emulator.js";
import type { CheckoutEvent, NodeEmulator, NodeEmulatorOptions } from "../money/node-emulator.js";

type Redirect = [url: string, kind: MoneyRedirectKind];
const copy = defaultMoneyMessages;

const directTransport = (node: NodeEmulator) => createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);

/** One page visit: a fresh client, transport and provider (whose key store reads the device's real localStorage). */
function visitFundsPage(
  node: NodeEmulator,
  redirects: Redirect[],
  props: Partial<AddFundsFormProps> = {},
  provider: Partial<AzoaMoneyProviderProps> = {},
) {
  return render(
    <StrictMode>
      <AzoaMoneyProvider transport={directTransport(node)} subject={node.userId} onRedirect={(url, kind) => redirects.push([url, kind])} {...provider}>
        <AddFundsForm currencyCode="usd" pollIntervalMs={10} {...props} />
      </AzoaMoneyProvider>
    </StrictMode>,
  );
}

/** Pays the first page: a visit that opens a checkout, then the host navigates away. */
async function openCheckout(node: NodeEmulator, redirects: Redirect[]) {
  const user = userEvent.setup();
  const visit = visitFundsPage(node, redirects);
  await user.type(await screen.findByLabelText("Amount"), "25");
  await user.click(screen.getByRole("button", { name: "Continue to payment" }));
  await waitFor(() => expect(redirects).toHaveLength(1));
  visit.unmount();
  return node.admissions[0]!.intentId;
}

const submitButton = () => screen.getByRole("button", { name: "Continue to payment" }) as HTMLButtonElement;
const posts = (node: NodeEmulator) => node.requests.filter(request => request.method === "POST");
const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
/** What this device has saved for the person's checkout (the request key is the lock against a second payment). */
const savedCheckout = () => readSavedRequest(createKeyStore({ subject: node.userId }), MONEY_SCOPES.checkout);

let node: NodeEmulator;
function useNode(options?: NodeEmulatorOptions) {
  node = createNodeEmulator(options);
  vi.stubGlobal("fetch", node.fetch);
  return node;
}

beforeEach(() => {
  localStorage.clear();
  useNode();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("<AddFundsForm> (P1 flows)", () => {
  it("a lost answer is offered for resume after a remount, and resuming redirects to the one payment exactly once", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const firstVisit = visitFundsPage(node, redirects);

    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    await screen.findByText("A payment of 25.00 USD has not been confirmed yet.");
    expect(redirects).toEqual([]);
    firstVisit.unmount();

    visitFundsPage(node, redirects);
    const resume = await screen.findByRole("button", { name: "Resume that payment" });
    // Resume is offered before (instead of) a new payment.
    expect(screen.queryByRole("button", { name: "Continue to payment" })).toBeNull();
    await user.click(resume);

    await waitFor(() => expect(redirects).toEqual([[node.admissions[0]!.paymentUrl, "checkout"]]));
    expect(node.admissions).toHaveLength(1);
    await screen.findByText(copy.settlement.not_started);
    expect(redirects).toHaveLength(1);
  });

  it("a complete payment whose funds have not landed keeps polling to funds_applied, locks the form meanwhile and announces once", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    // The real order: card paid, webhook marks the intent complete, the settlement inbox row is applied later.
    node.scriptReads(intentId, [["pay"], ["webhook"], [], [], [], [], ["applyFunds"]]);
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30, onSettled: view => settled.push(view) });

    // complete + payment_received: not final, and a second payment now would be a real second charge.
    await screen.findByText(copy.settlement.payment_received);
    expect(submitButton().disabled).toBe(true);
    expect(savedCheckout()?.serverId).toBe(intentId);
    expect(settled).toEqual([]);

    await screen.findByText(copy.settlement.funds_applied);
    expect(settled.map(view => [view.intentId, view.status, view.settlementState])).toEqual([[intentId, "complete", "funds_applied"]]);
    const readsAtSettlement = node.readsOf(intentId);
    await sleep(90);
    expect(node.readsOf(intentId)).toBe(readsAtSettlement);

    // The key was forgotten only once the money settled, so the same amount now opens a new payment.
    expect(savedCheckout()).toBeNull();
    expect(submitButton().disabled).toBe(false);
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toHaveLength(2));
    expect(node.admissions).toHaveLength(2);
    expect(redirects[1]).toEqual([node.admissions[1]!.paymentUrl, "checkout"]);
    expect(settled).toHaveLength(1);
  });

  // HIGH-1: the saved key names its intent, so the resend is decided by that intent's state, whatever the node's code says.
  it.each<[string, CheckoutEvent[], string, CheckoutEvent[]]>([
    ["an ACH payment still clearing after its page lapsed (the node resumes it)", ["payAsync", "webhook", "pageLapsed"], copy.checkout.clearing, ["asyncCleared", "webhook", "applyFunds"]],
    ["a card payment whose funds have not landed (409 INTENT_FINISHED)", ["pay", "webhook"], copy.settlement.payment_received, ["applyFunds"]],
  ])("resuming %s keeps the key and the form locked, shows the real state, then settles once", async (_label, timeline, shown, settle) => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.apply(intentId, ...timeline);
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { pollIntervalMs: 30, onSettled: view => settled.push(view) });
    await screen.findByText(shown);

    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Resume that payment" })).toBeNull());
    expect(savedCheckout()?.serverId).toBe(intentId);
    expect(submitButton().disabled).toBe(true);
    expect(screen.getAllByText(shown)).toHaveLength(1);
    expect(screen.queryByText("This payment page has expired. Start a new payment.")).toBeNull();
    expect(screen.queryByText("This payment is already finished.")).toBeNull();
    expect(redirects).toHaveLength(1);
    expect(settled).toEqual([]);

    node.apply(intentId, ...settle);
    await screen.findByText(copy.settlement.funds_applied);
    await waitFor(() => expect(submitButton().disabled).toBe(false));
    expect(settled.map(view => view.settlementState)).toEqual(["funds_applied"]);
    expect(savedCheckout()).toBeNull();
    expect(node.admissions).toHaveLength(1);
  });

  it("while the node cannot read Stripe nothing says it is waiting for a payment: polling continues, the key is kept, the form stays locked, and funds_applied lands once Stripe is back (MEDIUM-1)", async () => {
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.scriptReads(intentId, [["pay", "webhook", "stripeDown"], [], [], [], [], ["stripeUp", "applyFunds"]]);
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30, onSettled: view => settled.push(view) });

    await screen.findByText(copy.checkout.cannotConfirm);
    expect(screen.queryByText(copy.settlement.not_started)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.checkout.openPaymentPage })).toBeNull();
    expect(submitButton().disabled).toBe(true);
    expect(savedCheckout()?.serverId).toBe(intentId);
    const readsWhileDown = node.readsOf(intentId);
    await waitFor(() => expect(node.readsOf(intentId)).toBeGreaterThan(readsWhileDown));
    expect(settled).toEqual([]);

    await screen.findByText(copy.settlement.funds_applied);
    expect(settled.map(view => view.settlementState)).toEqual(["funds_applied"]);
    await waitFor(() => expect(submitButton().disabled).toBe(false));
  });

  it("after a good view, failed reads stop offering the payment page as if it were current and say it cannot be confirmed, keeping the key (LOW-4)", async () => {
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.scriptReads(intentId, [[], [], [], [], ["stripeDown"], [], [], [], [], [], ["stripeUp"]]);
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30 });

    await screen.findByRole("button", { name: copy.checkout.openPaymentPage });
    expect(screen.getAllByText(copy.settlement.not_started)).toHaveLength(1);

    await screen.findByText(copy.checkout.cannotConfirm);
    expect(screen.queryByText(copy.settlement.not_started)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.checkout.openPaymentPage })).toBeNull();
    expect(savedCheckout()?.serverId).toBe(intentId);
    expect(submitButton().disabled).toBe(true);

    await screen.findByRole("button", { name: copy.checkout.openPaymentPage });
    expect(screen.getAllByText(copy.settlement.not_started)).toHaveLength(1);
  });

  it("a 409 INTENT_EXPIRED for a lost answer (no intent id saved) decides by its own projection, releases the key and offers a fresh payment", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const firstVisit = visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    await screen.findByRole("button", { name: "Resume that payment" });
    firstVisit.unmount();
    node.apply(node.admissions[0]!.intentId, "pageLapsed");
    const before = posts(node).length;

    visitFundsPage(node, redirects);
    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    // The 409's own projection already proves it closed and unpaid; the form shows that settlement
    // state directly, with no void round trip to learn it (one resend POST, not a second for the void).
    await screen.findByText(copy.settlement.payment_failed);
    expect(posts(node)).toHaveLength(before + 1);
    expect(screen.queryByRole("button", { name: "Resume that payment" })).toBeNull();
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toEqual([[node.admissions[1]!.paymentUrl, "checkout"]]));
  });

  /** Submits 25 whose first Stripe create fails (503 RAIL_BUSY): an unattached claim, offered for resume. */
  async function submitWithFailedCreate(redirects: Redirect[], props: Partial<AddFundsFormProps> = {}) {
    const user = userEvent.setup();
    const visit = visitFundsPage(node, redirects, props);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.failNextCreate();
    await user.click(submitButton());
    await screen.findByRole("button", { name: "Resume that payment" });
    return { user, visit, intentId: node.admissions[0]!.intentId, requestKey: node.admissions[0]!.requestKey };
  }

  it("a payment the node holds for review keeps its key and is never resumed as lost or replaced, on this visit or the next; its Discard asks the node, which keeps it held on 33168340, and checking again after the operator closes it frees the form (MEDIUM-3, M2b)", async () => {
    useNode({ unreplayableClaimVoid: "needsReview" });
    const redirects: Redirect[] = [];
    const refusals: MoneyRefusal[] = [];
    const { user, visit, intentId, requestKey } = await submitWithFailedCreate(redirects, { onRefusal: refusal => refusals.push(refusal) });
    node.apply(intentId, "needsOperator");

    await user.click(screen.getByRole("button", { name: "Resume that payment" }));

    const heldForReview = async () => {
      await screen.findByText(copy.refusal.needs_review!);
      expect(screen.queryByRole("button", { name: "Resume that payment" })).toBeNull();
      // Never without an exit: discarding stays offered, and the node's void decides it (M2b).
      expect((screen.getByRole("button", { name: copy.pending.discard }) as HTMLButtonElement).disabled).toBe(false);
      expect(screen.queryByRole("button", { name: "Continue to payment" })).toBeNull();
      expect(screen.queryByText(/The answer did not arrive/)).toBeNull();
      expect(savedCheckout()).toMatchObject({ requestKey, needsReview: true });
    };
    await heldForReview();
    await user.click(screen.getByRole("button", { name: copy.pending.discard }));
    await waitFor(() => expect(node.answers.at(-1)).toMatchObject({ status: 409, code: "INTENT_NEEDS_REVIEW", path: expect.stringMatching(CHECKOUT_VOID) }));
    await heldForReview();
    expect(screen.queryByText(copy.pending.discardUnconfirmed)).toBeNull();
    expect(node.isVoided(intentId)).toBe(false);
    // Both review answers named the intent; neither handed the host its admission key (subject + tenant ids, M1).
    expect(refusals.map(refusal => refusal.code)).toEqual(["rail_busy", "needs_review", "needs_review"]);
    expect(refusals[1]!.serverResult).toMatchObject({ intentId, status: "starting", paymentUrl: null });
    expect(refusals[2]!.serverResult).toMatchObject({ outcome: "revealed", intent: { intentId, status: "starting" } });
    expect(findForbiddenViewKeys(refusals)).toEqual([]);
    visit.unmount();
    visitFundsPage(node, redirects);
    await heldForReview();

    node.apply(intentId, "operatorClosed");
    await user.click(screen.getByRole("button", { name: copy.pending.checkAgain }));

    // INTENT_FINISHED alone is not proof, but its own projection here already shows closed and unpaid
    // (the operator's close, not a payment), so that state frees the key with no void round trip needed.
    await screen.findByText(copy.checkout.expired);
    expect(screen.queryByText("This payment is already finished.")).toBeNull();
    expect(submitButton().disabled).toBe(false);
    expect(savedCheckout()).toBeNull();
    expect(node.admissions).toHaveLength(1);
  });

  it("an unattached claim past its 23-hour replay window is held for review, and Discard is the node's void: it abandons the claim (voided), so the key goes and a fresh payment can start (B10, M2b)", async () => {
    const redirects: Redirect[] = [];
    const { user, intentId } = await submitWithFailedCreate(redirects);
    node.apply(intentId, "replayWindowLapsed");
    await user.click(screen.getByRole("button", { name: "Resume that payment" }));
    await screen.findByText(copy.refusal.needs_review!);

    await user.click(screen.getByRole("button", { name: copy.pending.discard }));

    await screen.findByText(copy.pending.voided);
    expect(node.isVoided(intentId)).toBe(true);
    expect(savedCheckout()).toBeNull();
    expect(screen.queryByText(copy.refusal.needs_review!)).toBeNull();
    expect(submitButton().disabled).toBe(false);
    await user.clear(screen.getByLabelText("Amount"));
    await user.type(screen.getByLabelText("Amount"), "40");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toEqual([[node.admissions[1]!.paymentUrl, "checkout"]]));
    expect(node.admissions.map(admission => admission.amountMinorUnits)).toEqual([25_00, 40_00]);
  });

  it("an unattached claim resent minutes later meets INTENT_EXPIRED on a `starting` claim: it says the payment never started and nothing was charged, releases the key and offers a fresh payment (B9, M2a)", async () => {
    const redirects: Redirect[] = [];
    const { user, visit, intentId } = await submitWithFailedCreate(redirects);
    visit.unmount();
    node.apply(intentId, "creationLapsed");

    visitFundsPage(node, redirects);
    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    await screen.findByText(copy.pending.neverStarted);
    // Never "waiting for your payment" for a page that never existed, and nothing left to resume or discard.
    expect(screen.queryByText(copy.settlement.not_started)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.pending.discard })).toBeNull();
    expect(screen.queryByRole("button", { name: copy.checkout.check })).toBeNull();
    expect(savedCheckout()).toBeNull();
    expect(submitButton().disabled).toBe(false);
    expect(posts(node).filter(request => CHECKOUT_VOID.test(request.path))).toEqual([]);
    await user.clear(screen.getByLabelText("Amount"));
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toEqual([[node.admissions[1]!.paymentUrl, "checkout"]]));
    expect(node.admissions).toHaveLength(2);
  });

  it("a paid payment whose funds never land (a node without an issuance handler) can be acknowledged: the key goes, it says the payment was received and the funds are still being added, the form unlocks, and nothing new is started (M3)", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.apply(intentId, "pay", "webhook");
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30, onSettled: view => settled.push(view) });
    await screen.findByText(copy.settlement.payment_received);
    expect(submitButton().disabled).toBe(true);
    const postsBefore = posts(node).length;

    await user.click(screen.getByRole("button", { name: copy.checkout.acknowledge }));

    await screen.findByText(copy.checkout.acknowledged("25.00 USD"));
    expect(screen.queryByText(copy.settlement.funds_applied)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.checkout.check })).toBeNull();
    expect(savedCheckout()).toBeNull();
    expect(submitButton().disabled).toBe(false);
    const readsAfter = node.readsOf(intentId);
    await sleep(90);
    expect(node.readsOf(intentId)).toBe(readsAfter);
    expect(posts(node)).toHaveLength(postsBefore);
    expect(redirects).toHaveLength(1);
    expect(settled).toEqual([]);
    expect(node.admissions).toHaveLength(1);
  });

  it("a payment paid at Stripe but not yet closed (processing) offers nothing to acknowledge: polling continues, the key is kept and the form stays locked (M3)", async () => {
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.apply(intentId, "pay");
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30 });
    await screen.findByText(copy.settlement.payment_received);
    const reads = node.readsOf(intentId);

    await waitFor(() => expect(node.readsOf(intentId)).toBeGreaterThan(reads));

    expect(screen.queryByRole("button", { name: copy.checkout.acknowledge })).toBeNull();
    expect(submitButton().disabled).toBe(true);
    expect(savedCheckout()?.serverId).toBe(intentId);
  });

  it("resuming a payment another window replaced says it was replaced, never that it settled, and sends nothing (LOW-5)", async () => {
    const user = userEvent.setup();
    visitFundsPage(node, []);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    const resume = await screen.findByRole("button", { name: "Resume that payment" });
    // Another window discarded it and started a different amount, and this one heard no storage event.
    const otherWindow = createKeyStore({ subject: node.userId });
    otherWindow.remove(MONEY_SCOPES.checkout);
    claimRequestKey(otherWindow, MONEY_SCOPES.checkout, 40_00);
    const before = posts(node).length;

    await user.click(resume);

    await screen.findByText(copy.pending.replaced);
    expect(screen.queryByText(/settled/)).toBeNull();
    await screen.findByText("A payment of 40.00 USD has not been confirmed yet.");
    expect(posts(node)).toHaveLength(before);
  });

  it("discarding a payment another window already replaced voids nothing, so that window's new payment survives", async () => {
    const user = userEvent.setup();
    visitFundsPage(node, []);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    const discard = await screen.findByRole("button", { name: copy.pending.discard });
    // Another window discarded it and saved a different payment, and this one heard no storage event.
    const otherWindow = createKeyStore({ subject: node.userId });
    otherWindow.remove(MONEY_SCOPES.checkout);
    const replacement = claimRequestKey(otherWindow, MONEY_SCOPES.checkout, 40_00);

    await user.click(discard);

    await screen.findByText(copy.pending.replaced);
    expect(posts(node).filter(request => request.path.endsWith("/void"))).toEqual([]);
    expect(savedCheckout()?.requestKey).toBe(replacement.kind === "ready" ? replacement.request.requestKey : null);
  });

  /** A saved payment offered as pending: its answer was lost (no intent id saved), or its page opened and the person left. */
  async function savePendingPayment(lostAnswer: boolean, redirects: Redirect[]) {
    const user = userEvent.setup();
    if (lostAnswer) {
      visitFundsPage(node, redirects);
      await user.type(await screen.findByLabelText("Amount"), "25");
      node.loseNextResponse();
      await user.click(submitButton());
    } else {
      await openCheckout(node, redirects);
      visitFundsPage(node, redirects);
    }
    await screen.findByRole("button", { name: copy.pending.discard });
    return { user, intentId: node.admissions[0]!.intentId };
  }

  it.each([["whose answer was lost", true], ["whose page opened", false]])(
    "discarding an unpaid payment %s: the node expires its page and voids the key, the payment stops being shown, and a separate payment can start",
    async (_label, lostAnswer) => {
      const redirects: Redirect[] = [];
      const { user, intentId } = await savePendingPayment(lostAnswer, redirects);
      const redirectsBefore = redirects.length;

      await user.click(screen.getByRole("button", { name: copy.pending.discard }));

      await screen.findByText(copy.pending.voided);
      expect(node.isVoided(intentId)).toBe(true);
      expect(savedCheckout()).toBeNull();
      expect(screen.queryByRole("button", { name: copy.checkout.check })).toBeNull();
      expect(submitButton().disabled).toBe(false);
      await user.clear(screen.getByLabelText("Amount"));
      await user.type(screen.getByLabelText("Amount"), "40");
      await user.click(submitButton());

      await waitFor(() => expect(redirects).toHaveLength(redirectsBefore + 1));
      expect(redirects.at(-1)).toEqual([node.admissions[1]!.paymentUrl, "checkout"]);
      expect(node.admissions.map(admission => admission.amountMinorUnits)).toEqual([25_00, 40_00]);
    },
  );

  it.each([["whose answer was lost", true], ["whose page opened", false]])(
    "discarding a payment %s that was already paid reveals it: the key stays, the form stays locked, and funds_applied is announced once",
    async (_label, lostAnswer) => {
      const redirects: Redirect[] = [];
      const settled: CheckoutView[] = [];
      const user = userEvent.setup();
      if (lostAnswer) {
        visitFundsPage(node, redirects, { pollIntervalMs: 30, onSettled: view => settled.push(view) });
        await user.type(await screen.findByLabelText("Amount"), "25");
        node.loseNextResponse();
        await user.click(submitButton());
        // The person paid on that page anyway (e.g. from another device that got it); no webhook yet.
        node.apply(node.admissions[0]!.intentId, "pay");
      } else {
        const opened = await openCheckout(node, redirects);
        node.apply(opened, "pay");
        visitFundsPage(node, redirects, { pollIntervalMs: 30, onSettled: view => settled.push(view) });
      }
      const intentId = node.admissions[0]!.intentId;

      await user.click(await screen.findByRole("button", { name: copy.pending.discard }));

      await screen.findByText(copy.pending.revealed);
      expect(node.isVoided(intentId)).toBe(false);
      expect(savedCheckout()?.serverId).toBe(intentId);
      expect(submitButton().disabled).toBe(true);
      expect(screen.getAllByText(copy.settlement.payment_received)).toHaveLength(1);
      expect(settled).toEqual([]);

      node.apply(intentId, "webhook", "applyFunds");
      await screen.findByText(copy.settlement.funds_applied);
      await waitFor(() => expect(submitButton().disabled).toBe(false));
      await sleep(90);
      expect(settled.map(view => [view.intentId, view.settlementState])).toEqual([[intentId, "funds_applied"]]);
      expect(savedCheckout()).toBeNull();
      expect(node.admissions).toHaveLength(1);
    },
  );

  it("a discard the node cannot decide (it cannot read Stripe) keeps the payment and says so; once Stripe is readable again, discarding voids it", async () => {
    const redirects: Redirect[] = [];
    const { user, intentId } = await savePendingPayment(true, redirects);
    node.apply(intentId, "stripeDown");

    await user.click(screen.getByRole("button", { name: copy.pending.discard }));

    await screen.findByText(copy.pending.discardUnconfirmed);
    expect(screen.getByRole("button", { name: "Resume that payment" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Continue to payment" })).toBeNull();
    expect(savedCheckout()?.requestKey).toBe(node.admissions[0]!.requestKey);
    expect(node.isVoided(intentId)).toBe(false);

    node.apply(intentId, "stripeUp");
    await user.click(screen.getByRole("button", { name: copy.pending.discard }));
    await screen.findByText(copy.pending.voided);
    expect(savedCheckout()).toBeNull();
    expect(node.isVoided(intentId)).toBe(true);
  });

  it("resuming a lost answer whose payment was made meets INTENT_FINISHED; its own unsettled projection decides it directly, so the key and the lock stay and nothing is charged twice", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { pollIntervalMs: 30, onSettled: view => settled.push(view) });
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    const intentId = node.admissions[0]!.intentId;
    node.apply(intentId, "pay", "webhook");

    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    await screen.findByText(copy.settlement.payment_received);
    expect(screen.queryByText("This payment is already finished.")).toBeNull();
    expect(savedCheckout()?.serverId).toBe(intentId);
    expect(submitButton().disabled).toBe(true);
    expect(redirects).toEqual([]);

    node.apply(intentId, "applyFunds");
    await screen.findByText(copy.settlement.funds_applied);
    await waitFor(() => expect(submitButton().disabled).toBe(false));
    expect(settled.map(view => view.settlementState)).toEqual(["funds_applied"]);
    expect(savedCheckout()).toBeNull();
    expect(node.admissions).toHaveLength(1);
  });

  it("a return link naming a payment the node does not know does not lock the form", async () => {
    const user = userEvent.setup();
    visitFundsPage(node, [], { returnedIntentId: newRequestKey() });

    await screen.findByText("That payment could not be found.");
    await user.type(screen.getByLabelText("Amount"), "25");
    expect(submitButton().disabled).toBe(false);
  });

  it("a refusal code the SDK does not know keeps the key, so the payment is offered for resume, not replaced", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const direct = directTransport(node);
    const strange: AzoaMoneyTransport = {
      ...direct,
      checkout: { ...direct.checkout, begin: async () => ({ ok: false, refusal: { code: "server_error" as never, message: "Upstream timed out." } }) },
    };
    const visit = render(
      <AzoaMoneyProvider transport={strange} subject={node.userId} onRedirect={(url, kind) => redirects.push([url, kind])}>
        <AddFundsForm currencyCode="usd" />
      </AzoaMoneyProvider>,
    );
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());

    await screen.findByText("A payment of 25.00 USD has not been confirmed yet.");
    visit.unmount();
    visitFundsPage(node, redirects);
    await screen.findByRole("button", { name: "Resume that payment" });
  });

  it("the same-tick double click sends once and redirects once", async () => {
    const redirects: Redirect[] = [];
    const transport = directTransport(node);
    const wrapper = ({ children }: { children?: ReactNode }) => (
      <AzoaMoneyProvider transport={transport} subject={node.userId} onRedirect={(url, kind) => redirects.push([url, kind])}>{children}</AzoaMoneyProvider>
    );
    const { result } = renderHook(() => useAddFunds(), { wrapper });

    await act(async () => {
      await Promise.all([result.current.start(25_00), result.current.start(25_00)]);
    });

    expect(posts(node)).toHaveLength(1);
    expect(redirects).toHaveLength(1);
  });

  it("an answer that arrives after the page unmounted never redirects, and the next visit finds the payment by its key", async () => {
    useNode({ latencyMs: 30 });
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const visit = visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());
    visit.unmount();

    await waitFor(() => expect(node.admissions).toHaveLength(1));
    await sleep(60);
    expect(redirects).toEqual([]);

    visitFundsPage(node, redirects);
    await screen.findByText(copy.settlement.not_started);
    expect(node.readsOf(node.admissions[0]!.intentId)).toBeGreaterThan(0);
  });

  it("announces only to a device that waited: once on the first settled read for a held key, never on a reload or a refresh", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.apply(intentId, "pay", "webhook", "applyFunds");
    const settled: CheckoutView[] = [];

    // This device still holds the key, so its first (already settled) read is the outcome it waited for.
    const returnVisit = visitFundsPage(node, redirects, { returnedIntentId: intentId, onSettled: view => settled.push(view) });
    await screen.findByText(copy.settlement.funds_applied);
    await waitFor(() => expect(settled).toHaveLength(1));
    const check = screen.getByRole("button", { name: copy.checkout.check });
    await waitFor(() => expect((check as HTMLButtonElement).disabled).toBe(false));
    await user.click(check);
    await sleep(40);
    expect(settled).toHaveLength(1);
    returnVisit.unmount();

    // Reloading the return URL: the key is resolved and nothing was watched here, so it is not "funds added" again.
    visitFundsPage(node, redirects, { returnedIntentId: intentId, onSettled: view => settled.push(view) });
    await screen.findByText(copy.settlement.funds_applied);
    await sleep(40);
    expect(settled).toHaveLength(1);
  });

  it("follows another tab's changes to the saved payment both ways (storage events)", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    await screen.findByRole("button", { name: "Resume that payment" });

    // Another tab resolved it (`storage` fires only in the other tabs).
    const slot = Object.keys(localStorage).find(key => key.startsWith(`azoa-sdk:money:${MONEY_SCOPES.checkout}:`))!;
    localStorage.removeItem(slot);
    act(() => void window.dispatchEvent(new StorageEvent("storage", { key: slot })));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Resume that payment" })).toBeNull());

    // Another tab lost an answer of its own: this tab must not offer a second, separate payment.
    claimRequestKey(createKeyStore({ subject: node.userId }), MONEY_SCOPES.checkout, 40_00);
    act(() => void window.dispatchEvent(new StorageEvent("storage", { key: slot })));
    await screen.findByText("A payment of 40.00 USD has not been confirmed yet.");
  });

  it("a host onRedirect that throws does not turn an opened payment into a fake unconfirmed one, nor leave the form busy", async () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const user = userEvent.setup();
    render(
      <AzoaMoneyProvider transport={directTransport(node)} subject={node.userId} onRedirect={() => { throw new Error("router not ready"); }}>
        <AddFundsForm currencyCode="usd" pollIntervalMs={10} />
      </AzoaMoneyProvider>,
    );
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());

    await screen.findByText(copy.settlement.not_started);
    expect(screen.queryByText(/has not been confirmed yet/)).toBeNull();
    expect(errors).toHaveBeenCalledWith(expect.stringContaining("a host callback threw"), expect.any(Error));
    // The open page holds the form; once its money settles the form is usable again (nothing stayed busy).
    node.apply(node.admissions[0]!.intentId, "pay", "webhook", "applyFunds");
    await waitFor(() => expect(submitButton().disabled).toBe(false));
  });

  it("hands the host only the node's provider pages by default; a host lists any other origin it uses (LOW-6)", async () => {
    // A Stripe Checkout custom domain is a different origin from checkout.stripe.com.
    useNode({ paymentOrigin: "https://pay.example.com" });
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const visit = visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());
    await screen.findByText(copy.settlement.not_started);
    expect(redirects).toEqual([]);
    expect(screen.queryByRole("button", { name: copy.checkout.openPaymentPage })).toBeNull();
    visit.unmount();

    visitFundsPage(node, redirects, {}, { allowedRedirectOrigins: ["https://pay.example.com"] });
    await user.click(await screen.findByRole("button", { name: copy.checkout.openPaymentPage }));
    expect(redirects).toEqual([[node.admissions[0]!.paymentUrl, "checkout"]]);
  });

  it.each([
    ["jpy", "2500", 2500],
    ["kwd", "1.5", 1500],
  ])("parses amounts with the minor units of %s", async (currencyCode, typed, minorUnits) => {
    const user = userEvent.setup();
    visitFundsPage(node, [], { currencyCode });
    await user.type(await screen.findByLabelText("Amount"), typed);
    await user.click(submitButton());
    await waitFor(() => expect(posts(node)).toHaveLength(1));
    expect(posts(node)[0]!.body.amountMinorUnits).toBe(minorUnits);
  });

  it("an unknown currency accepts no amount and sends nothing", async () => {
    const user = userEvent.setup();
    visitFundsPage(node, [], { currencyCode: "xyz" });
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());
    await screen.findByText(copy.common.unknownCurrency);
    expect(posts(node)).toEqual([]);
  });
});
