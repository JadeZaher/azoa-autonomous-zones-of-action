// @vitest-environment jsdom
import { StrictMode } from "react";
import type { ReactNode } from "react";
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { MONEY_SCOPES, claimRequestKey, createDirectMoneyTransport, createKeyStore, defaultMoneyMessages } from "../../src/money/index.js";
import type { AzoaMoneyTransport, CheckoutView, MoneyRedirectKind } from "../../src/money/index.js";
import { AddFundsForm, AzoaMoneyProvider, useAddFunds } from "../../src/react/index.js";
import type { AddFundsFormProps, AzoaMoneyProviderProps } from "../../src/react/index.js";
import { createNodeEmulator } from "../money/node-emulator.js";
import type { NodeEmulator, NodeEmulatorOptions } from "../money/node-emulator.js";

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
    await waitFor(() => expect(submitButton().disabled).toBe(true));
    expect(screen.getByText(copy.settlement.payment_received)).toBeTruthy();
    expect(settled).toEqual([]);

    await screen.findByText(copy.settlement.funds_applied);
    expect(settled.map(view => [view.intentId, view.status, view.settlementState])).toEqual([[intentId, "complete", "funds_applied"]]);
    const readsAtSettlement = node.readsOf(intentId);
    await sleep(90);
    expect(node.readsOf(intentId)).toBe(readsAtSettlement);

    // The key was forgotten once the intent was terminal, so the same amount now opens a new payment.
    expect(submitButton().disabled).toBe(false);
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toHaveLength(2));
    expect(node.admissions).toHaveLength(2);
    expect(redirects[1]).toEqual([node.admissions[1]!.paymentUrl, "checkout"]);
    expect(settled).toHaveLength(1);
  });

  it("complete with nothing cleared yet (the node's Stripe re-read failed) never says it is waiting for a payment", async () => {
    const redirects: Redirect[] = [];
    const intentId = await openCheckout(node, redirects);
    node.scriptReads(intentId, [["pay", "webhook", "stripeDown"], [], [], [], [], ["stripeUp", "applyFunds"]]);
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { returnedIntentId: intentId, pollIntervalMs: 30, onSettled: view => settled.push(view) });

    await screen.findByText(copy.checkout.confirming);
    expect(screen.queryByText(copy.settlement.not_started)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.checkout.openPaymentPage })).toBeNull();
    expect(settled).toEqual([]);

    await screen.findByText(copy.settlement.funds_applied);
    expect(settled.map(view => view.settlementState)).toEqual(["funds_applied"]);
  });

  it("a 409 INTENT_EXPIRED on resume releases the key, shows the node's sentence and offers a fresh payment", async () => {
    useNode({ conflictCodes: "typed" });
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const firstVisit = visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextResponse();
    await user.click(submitButton());
    await screen.findByRole("button", { name: "Resume that payment" });
    firstVisit.unmount();
    node.apply(node.admissions[0]!.intentId, "pageLapsed");

    visitFundsPage(node, redirects);
    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    await screen.findByText("This payment page has expired. Start a new payment.");
    expect(screen.queryByRole("button", { name: "Resume that payment" })).toBeNull();
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(submitButton());
    await waitFor(() => expect(redirects).toEqual([[node.admissions[1]!.paymentUrl, "checkout"]]));
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

  it("a host onRedirect that throws does not turn an opened payment into a fake unconfirmed one", async () => {
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
    expect(submitButton().disabled).toBe(false);
    expect(errors).toHaveBeenCalledWith(expect.stringContaining("a host callback threw"), expect.any(Error));
  });

  it("hands the host only payment pages on an allowed origin", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const visit = visitFundsPage(node, redirects, {}, { allowedRedirectOrigins: ["https://checkout.stripe.com"] });
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(submitButton());
    await screen.findByText(copy.settlement.not_started);
    expect(redirects).toEqual([]);
    expect(screen.queryByRole("button", { name: copy.checkout.openPaymentPage })).toBeNull();
    visit.unmount();

    visitFundsPage(node, redirects, {}, { allowedRedirectOrigins: ["https://checkout.stripe.test"] });
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
