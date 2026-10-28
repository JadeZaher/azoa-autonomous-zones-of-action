// @vitest-environment jsdom
import { useLayoutEffect } from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { MONEY_SCOPES, claimRequestKey, createKeyStore, defaultMoneyMessages } from "../../src/money/index.js";
import type { AzoaMoneyTransport, KeyStore, MoneyRedirectKind } from "../../src/money/index.js";
import { AddFundsForm, AzoaMoneyProvider, ConsentStatus, CustodialAccountCard, KycStep, MoneyGate } from "../../src/react/index.js";
import type { AzoaMoneyProviderProps } from "../../src/react/index.js";
import { createTenantBff } from "./tenant-bff.js";
import type { Person } from "./tenant-bff.js";

type Redirect = [url: string, kind: MoneyRedirectKind];
const copy = defaultMoneyMessages;

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  cleanup();
});

describe("<MoneyGate> and the onboarding steps", () => {
  // [account state, what the gate renders, whether the money form is unlocked]
  it.each<[string, Partial<Person>, string, boolean]>([
    ["ready", { kycStatus: "Approved" }, "the money form", true],
    ["no account yet (setup)", { opened: false, kycStatus: "Unknown" }, copy.kycStatus.Unknown, false],
    ["verification not finished (setup)", { kycStatus: "Pending" }, copy.kyc.continue, false],
    ["compliance review", { kycStatus: "UnderReview", reviewDueAt: "2026-11-02T10:00:00Z" }, copy.gate.review, false],
    ["consent withdrawn", { consentRevoked: true }, copy.consent.revoked, false],
    ["node cannot onboard", { kycStatus: "Unknown", identityReady: false }, "Identity checks are offline on this node.", false],
  ])("%s", async (_label, person, shown, unlocked) => {
    const bff = createTenantBff({ ada: person });
    render(
      <AzoaMoneyProvider transport={bff.transport} subject="ada">
        <MoneyGate>
          <p>the money form</p>
        </MoneyGate>
      </AzoaMoneyProvider>,
    );

    await screen.findByText(shown, { exact: false });
    expect(screen.queryByText("the money form") !== null).toBe(unlocked);
  });

  it("verifying identity ensures the account first, then hands the verification page to the host once", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: { opened: false, kycStatus: "Unknown" } });
    const redirects: Redirect[] = [];
    render(
      <AzoaMoneyProvider transport={bff.transport} subject="ada" onRedirect={(url, kind) => redirects.push([url, kind])}>
        <KycStep />
      </AzoaMoneyProvider>,
    );

    await user.click(await screen.findByRole("button", { name: copy.kyc.start }));

    await waitFor(() => expect(redirects).toEqual([["https://widget.identomat.com/?session_token=ada", "kyc"]]));
    expect(bff.calls.filter(call => call.startsWith("account.ensure") || call.startsWith("account.beginKyc"))).toEqual(["account.ensure(ada)", "account.beginKyc(ada)"]);
    await user.click(screen.getByRole("button", { name: copy.kyc.checkAgain }));
    await screen.findByText(copy.kycStatus.Pending);
  });

  it("withdrawing consent needs a confirmation, and every component reading the one account store sees it at once", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: { kycStatus: "Approved" } });
    render(
      <AzoaMoneyProvider transport={bff.transport} subject="ada">
        <MoneyGate>
          <p>the money form</p>
        </MoneyGate>
        <CustodialAccountCard />
        <ConsentStatus />
      </AzoaMoneyProvider>,
    );
    await screen.findByText("the money form");
    // Three account readers, one read.
    expect(bff.calls.filter(call => call.startsWith("account.status"))).toHaveLength(1);

    await user.click(screen.getByRole("button", { name: copy.consent.revoke }));
    expect(bff.calls).not.toContain("account.revokeConsent(ada)");
    await user.click(screen.getByRole("button", { name: copy.consent.confirmRevoke }));

    await waitFor(() => expect(screen.queryByText("the money form")).toBeNull());
    // The gate now renders its own ConsentStatus beside the standalone one; the card reads the same store.
    expect(screen.getAllByText(copy.consent.revoked)).toHaveLength(2);
    expect(screen.getAllByText(copy.gate.revoked)).toHaveLength(1);
  });
});

describe("a same-tab user switch (M2): same transport object, new subject", () => {
  it("never shows the previous person's wallet, verification link or pending payment", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ alice: { kycStatus: "Pending" }, bob: { kycStatus: "Approved" } }, "alice");
    // Alice lost the answer to a payment on this device earlier.
    claimRequestKey(createKeyStore({ subject: "alice" }), MONEY_SCOPES.checkout, 25_00);
    const page = (subject: string, props: Partial<AzoaMoneyProviderProps> = {}) => (
      <AzoaMoneyProvider transport={bff.transport} subject={subject} {...props}>
        <CustodialAccountCard />
        <KycStep />
        <AddFundsForm currencyCode="usd" />
      </AzoaMoneyProvider>
    );
    const view = render(page("alice"));
    await screen.findByText("ALICE-WALLET");
    await screen.findByText("A payment of 25.00 USD has not been confirmed yet.");
    await user.click(screen.getByRole("button", { name: copy.kyc.continue }));
    const aliceLink = await screen.findByRole("link", { name: copy.kyc.openVerification });
    expect(aliceLink.getAttribute("href")).toBe("https://widget.identomat.com/?session_token=alice");

    bff.signIn("bob");
    view.rerender(page("bob"));

    await screen.findByText("BOB-WALLET");
    expect(screen.queryByText("ALICE-WALLET")).toBeNull();
    expect(screen.queryByRole("link", { name: copy.kyc.openVerification })).toBeNull();
    expect(screen.queryByText(/has not been confirmed yet/)).toBeNull();
    expect((screen.getByRole("button", { name: "Continue to payment" }) as HTMLButtonElement).disabled).toBe(false);
  });
});

describe("a host keeps one session-aware keyStore object across a user switch (LOW-3)", () => {
  it("the next person's first paint has none of the previous person's state, and the previous person's late answer never applies or redirects", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ alice: {}, bob: {} }, "alice");
    let signedIn = "alice";
    const storeFor = () => createKeyStore({ subject: signedIn });
    // One object for the whole app that routes to whoever is signed in now, so the provider's key store never changes.
    const sessionKeyStore: KeyStore = {
      read: scope => storeFor().read(scope),
      write: (scope, value) => storeFor().write(scope, value),
      remove: scope => storeFor().remove(scope),
    };
    // Alice lost the answer to a payment on this device earlier.
    claimRequestKey(storeFor(), MONEY_SCOPES.checkout, 25_00);
    let answerAlice!: () => void;
    const aliceAnswered = new Promise<void>(resolve => (answerAlice = resolve));
    const transport: AzoaMoneyTransport = {
      ...bff.transport,
      // Alice's resend is admitted at once (as alice), but its answer is held on the wire.
      checkout: { ...bff.transport.checkout, begin: request => {
        const answer = bff.transport.checkout.begin(request);
        return aliceAnswered.then(() => answer);
      } },
    };
    const redirects: Redirect[] = [];
    const paints: [subject: string, text: string][] = [];
    const page = (subject: string) => (
      <AzoaMoneyProvider transport={transport} keyStore={sessionKeyStore} subject={subject} onRedirect={(url, kind) => redirects.push([url, kind])}>
        <AddFundsForm currencyCode="usd" />
        <PaintProbe subject={subject} paints={paints} />
      </AzoaMoneyProvider>
    );
    const view = render(page("alice"));
    await user.click(await screen.findByRole("button", { name: "Resume that payment" }));

    signedIn = "bob";
    bff.signIn("bob");
    view.rerender(page("bob"));

    const [subject, firstPaint] = paints.at(-1)!;
    expect(subject).toBe("bob");
    expect(firstPaint).not.toContain("has not been confirmed yet");
    expect((screen.getByRole("button", { name: "Continue to payment" }) as HTMLButtonElement).disabled).toBe(false);
    await act(async () => {
      answerAlice();
      await aliceAnswered;
    });
    await new Promise(resolve => setTimeout(resolve, 30));
    expect(redirects).toEqual([]);
    expect(screen.queryByText(copy.settlement.not_started)).toBeNull();
    expect((screen.getByRole("button", { name: "Continue to payment" }) as HTMLButtonElement).disabled).toBe(false);
  });
});

/** Records the page's text at the commit where `subject` first renders: layout effects run before any passive effect could reset state. */
function PaintProbe({ subject, paints }: { subject: string; paints: [string, string][] }) {
  useLayoutEffect(() => {
    paints.push([subject, document.body.textContent ?? ""]);
  }, [subject, paints]);
  return null;
}
