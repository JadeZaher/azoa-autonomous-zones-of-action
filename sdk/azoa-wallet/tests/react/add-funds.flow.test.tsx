// @vitest-environment jsdom
import { StrictMode } from "react";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { createDirectMoneyTransport } from "../../src/money/index.js";
import type { CheckoutView, MoneyRedirectKind } from "../../src/money/index.js";
import { AddFundsForm, AzoaMoneyProvider } from "../../src/react/index.js";
import type { AddFundsFormProps } from "../../src/react/index.js";
import { createNodeEmulator } from "../money/node-emulator.js";
import type { NodeEmulator } from "../money/node-emulator.js";

type Redirect = [url: string, kind: MoneyRedirectKind];

/** One page visit: a fresh client, transport and provider (whose key store reads the device's real localStorage). */
function visitFundsPage(node: NodeEmulator, redirects: Redirect[], props: AddFundsFormProps = {}) {
  const transport = createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
  return render(
    <StrictMode>
      <AzoaMoneyProvider transport={transport} subject={node.userId} onRedirect={(url, kind) => redirects.push([url, kind])}>
        <AddFundsForm currencyCode="usd" pollIntervalMs={10} {...props} />
      </AzoaMoneyProvider>
    </StrictMode>,
  );
}

let node: NodeEmulator;

beforeEach(() => {
  localStorage.clear();
  node = createNodeEmulator();
  vi.stubGlobal("fetch", node.fetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("<AddFundsForm> (P1 flows)", () => {
  it("a lost answer is offered for resume after a remount, and resuming redirects to the one payment exactly once", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const firstVisit = visitFundsPage(node, redirects);

    await user.type(await screen.findByLabelText("Amount"), "25");
    node.loseNextAdmissionResponse();
    await user.click(screen.getByRole("button", { name: "Continue to payment" }));
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
    await screen.findByText("Waiting for your payment.");
    expect(redirects).toHaveLength(1);
  });

  it("returning with the intent id polls to the funds-applied copy, announces settlement once and frees the key", async () => {
    const user = userEvent.setup();
    const redirects: Redirect[] = [];
    const firstVisit = visitFundsPage(node, redirects);
    await user.type(await screen.findByLabelText("Amount"), "25");
    await user.click(screen.getByRole("button", { name: "Continue to payment" }));
    await waitFor(() => expect(redirects).toHaveLength(1));
    firstVisit.unmount(); // The host navigated to the payment page.

    const intentId = node.admissions[0]!.intentId;
    node.progress(intentId, ["payment_received", "payment_received", "funds_applied"]);
    const settled: CheckoutView[] = [];
    visitFundsPage(node, redirects, { returnedIntentId: intentId, onSettled: view => settled.push(view) });

    await screen.findByText("The funds are in your account.");
    expect(settled.map(view => [view.intentId, view.settlementState])).toEqual([[intentId, "funds_applied"]]);
    const readsAtSettlement = node.readsOf(intentId);
    await new Promise(resolve => setTimeout(resolve, 60));
    expect(node.readsOf(intentId)).toBe(readsAtSettlement);

    // The key was resolved by state, so the same amount now opens a new payment instead of resuming the paid one.
    await user.type(screen.getByLabelText("Amount"), "25");
    await user.click(screen.getByRole("button", { name: "Continue to payment" }));
    await waitFor(() => expect(redirects).toHaveLength(2));
    expect(node.admissions).toHaveLength(2);
    expect(redirects[1]).toEqual([node.admissions[1]!.paymentUrl, "checkout"]);
    expect(settled).toHaveLength(1);
  });
});
