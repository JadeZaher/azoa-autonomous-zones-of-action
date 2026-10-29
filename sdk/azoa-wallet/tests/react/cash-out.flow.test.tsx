// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { MONEY_SCOPES, createDirectMoneyTransport, defaultMoneyMessages, newRequestKey } from "../../src/money/index.js";
import type { AzoaMoneyTransport, CashOutView, MoneyRedirectKind } from "../../src/money/index.js";
import { AzoaMoneyProvider, PayoutDestinationStep, WithdrawForm } from "../../src/react/index.js";
import type { WithdrawFormProps } from "../../src/react/index.js";
import { CASH_OUT_START, createNodeEmulator } from "../money/node-emulator.js";
import type { NodeEmulator } from "../money/node-emulator.js";
import { createTenantBff } from "./tenant-bff.js";

const copy = defaultMoneyMessages;
const directTransport = (node: NodeEmulator) => createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
const cashOutButton = () => screen.getByRole("button", { name: copy.cashOut.submit }) as HTMLButtonElement;
/** The tone of the alert saying `text` (history rows repeat status sentences, so only alerts are searched). */
const toneOf = (text: string) => screen.getByText(text, { selector: "[data-tone]" }).getAttribute("data-tone");
const cashOutPosts = (node: NodeEmulator) => node.requests.filter(request => request.method === "POST" && CASH_OUT_START.test(request.path));

function visitWithdrawPage(node: NodeEmulator, props: Partial<WithdrawFormProps> = {}, transport: AzoaMoneyTransport = directTransport(node)) {
  return render(
    <AzoaMoneyProvider transport={transport} subject={node.userId}>
      <WithdrawForm {...props} />
    </AzoaMoneyProvider>,
  );
}

async function cashOut(amount: string) {
  const user = userEvent.setup();
  const field = await screen.findByLabelText(copy.cashOut.amountLabel);
  await waitFor(() => expect(cashOutButton().disabled).toBe(false));
  await user.clear(field);
  await user.type(field, amount);
  await user.click(cashOutButton());
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
  vi.restoreAllMocks();
});

describe("<WithdrawForm>", () => {
  it("a cash-out is accepted once, announced once and listed in the history it re-reads", async () => {
    const settled: CashOutView[] = [];
    visitWithdrawPage(node, { onSettled: view => settled.push(view) });

    await cashOut("10");

    await screen.findByText("Getting your money ready to send.", { selector: "[data-tone]" });
    expect(toneOf("Getting your money ready to send.")).toBe("success");
    expect(settled.map(view => [view.status, view.amountMinorUnits])).toEqual([["Requested", 10_00]]);
    await screen.findByText("10.00 USD");
    expect(node.cashOuts).toHaveLength(1);
  });

  it("a same-key replay of a refused cash-out (a 200 carrying the refused row) shows the refusal, never success", async () => {
    const settled: CashOutView[] = [];
    visitWithdrawPage(node, { onSettled: view => settled.push(view) });
    await screen.findByText("100.00 USD");
    // Another device takes most of the balance after this page read it; the node then refuses this one durably.
    await directTransport(node).cashOut.start({ requestKey: newRequestKey(), amountMinorUnits: 60_00 });
    node.loseNextResponse(CASH_OUT_START);
    await cashOut("50");
    await userEvent.setup().click(await screen.findByRole("button", { name: copy.pending.resume.cashOut }));

    await screen.findByText("That is more than you have available to cash out.", { selector: "[data-tone]" });
    expect(toneOf("That is more than you have available to cash out.")).toBe("error");
    expect(settled).toEqual([]);
    // Released: the form is back instead of a resume offer.
    expect(screen.queryByRole("button", { name: copy.pending.resume.cashOut })).toBeNull();
  });

  it("a host onSettled that throws leaves the form usable for the next cash-out", async () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    visitWithdrawPage(node, { onSettled: () => { throw new Error("analytics down"); } });

    await cashOut("10");
    await waitFor(() => expect(node.cashOuts).toHaveLength(1));
    await waitFor(() => expect(cashOutButton().disabled).toBe(false));
    await cashOut("5");

    await waitFor(() => expect(node.cashOuts).toHaveLength(2));
    expect(errors).toHaveBeenCalled();
  });

  it("a host transport answering ok with no view is unconfirmed: the key survives and the form is not stuck busy", async () => {
    const direct = directTransport(node);
    const broken: AzoaMoneyTransport = { ...direct, cashOut: { ...direct.cashOut, start: async () => ({ ok: true, value: null as unknown as CashOutView }) } };
    visitWithdrawPage(node, {}, broken);

    await cashOut("10");

    const resume = await screen.findByRole("button", { name: copy.pending.resume.cashOut });
    await waitFor(() => expect((resume as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByText("A cash-out of 10.00 USD has not been confirmed yet.")).not.toBeNull();
  });

  it("resuming a cash-out another tab already resolved sends nothing", async () => {
    visitWithdrawPage(node);
    node.loseNextResponse(CASH_OUT_START);
    await cashOut("10");
    const resume = await screen.findByRole("button", { name: copy.pending.resume.cashOut });
    // The other tab resolved it without this one hearing a storage event.
    const slot = Object.keys(localStorage).find(key => key.startsWith(`azoa-sdk:money:${MONEY_SCOPES.cashOut}:`))!;
    localStorage.removeItem(slot);
    const before = cashOutPosts(node).length;

    await act(async () => {
      resume.click();
    });

    await screen.findByText(copy.pending.superseded);
    // It may have been discarded there, so nothing claims it settled (LOW-5).
    expect(screen.queryByText(/settled/)).toBeNull();
    expect(cashOutPosts(node)).toHaveLength(before);
  });
});

describe("<PayoutDestinationStep> through a tenant BFF (F-H8)", () => {
  it("a node refusal of tenant-initiated setup is a route to the account holder, not an error", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: {} });
    const redirects: [string, MoneyRedirectKind][] = [];
    render(
      // The host's own account page is not a provider origin, so the host lists it.
      <AzoaMoneyProvider transport={bff.transport} subject="ada" allowedRedirectOrigins={["https://app.azoa.example"]} onRedirect={(url, kind) => redirects.push([url, kind])}>
        <PayoutDestinationStep accountHolderSetupUrl="https://app.azoa.example/payouts" />
      </AzoaMoneyProvider>,
    );

    await user.click(await screen.findByRole("button", { name: copy.destination.start }));

    await screen.findByText(copy.destination.accountHolderOnly);
    expect(toneOf(copy.destination.accountHolderOnly)).toBe("info");
    await user.click(screen.getByRole("button", { name: copy.destination.accountHolderAction }));
    expect(redirects).toEqual([["https://app.azoa.example/payouts", "payout-setup"]]);
  });
});
