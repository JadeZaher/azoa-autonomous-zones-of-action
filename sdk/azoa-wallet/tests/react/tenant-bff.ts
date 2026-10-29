import { randomUUID } from "node:crypto";
import type { TenantKycStatus } from "../../src/api/client.js";
import type {
  AzoaMoneyTransport,
  CashOutView,
  CheckoutView,
  CustodialAccountView,
  MoneyRefusal,
  MoneyResult,
  SaleQuoteView,
  SaleView,
} from "../../src/money/index.js";

/**
 * A host BFF (ArdaNova's shape: tRPC → BFF → AZOA tenant routes) as one in-memory `AzoaMoneyTransport`. The transport
 * object is session-agnostic, exactly like a BFF's: `signIn` changes whose session it answers for, not the object.
 * Tenant behavior mirrors the node: no account → `setup_required`; withdrawn consent → `consent_revoked` on every call;
 * tenant-initiated payout setup → `account_holder_required` (F-H8).
 */
export interface TenantBff {
  transport: AzoaMoneyTransport;
  signIn(user: string): void;
  person(user: string): Person;
  /** Every transport call, as `section.method(user)`. */
  readonly calls: string[];
  /** Every sale the host accepted. */
  readonly sales: (SaleView & { quoteId: string; requestKey: string })[];
  /** The next `sale.sell` is accepted, then its answer is lost. */
  loseNextSale(): void;
}

export interface Person {
  opened: boolean;
  kycStatus: TenantKycStatus;
  walletAddress: string;
  consentRevoked: boolean;
  reviewDueAt: string | null;
  identityReady: boolean;
}

export function createTenantBff(people: Record<string, Partial<Person>>, firstUser = Object.keys(people)[0]!): TenantBff {
  const records = new Map<string, Person>(Object.entries(people).map(([user, person]) => [user, {
    opened: true, kycStatus: "Approved", walletAddress: `${user.toUpperCase()}-WALLET`, consentRevoked: false, reviewDueAt: null, identityReady: true, ...person,
  }]));
  const intents = new Map<string, CheckoutView & { owner: string; requestKey: string }>();
  /** `${owner}:${requestKey}` for every key the node voided; begin under one is refused (REQUEST_VOIDED). */
  const voidedKeys = new Set<string>();
  const cashOuts = new Map<string, CashOutView & { owner: string }>();
  const quotes = new Map<string, SaleQuoteView>();
  const sales: (SaleView & { quoteId: string; requestKey: string; owner: string })[] = [];
  const calls: string[] = [];
  let user = firstUser;
  let loseSale = false;

  const me = () => records.get(user)!;
  const refuse = (code: MoneyRefusal["code"], message: string): MoneyResult<never> => ({ ok: false, refusal: { code, message } });
  const consentGate = (): MoneyResult<never> | null => (me().consentRevoked ? refuse("consent_revoked", "You withdrew this app's permission.") : null);
  const answer = async <T>(label: string, run: () => MoneyResult<T>): Promise<MoneyResult<T>> => {
    calls.push(`${label}(${user})`);
    await Promise.resolve();
    return consentGate() ?? run();
  };
  const accountView = (): CustodialAccountView => {
    const person = me();
    const approved = person.kycStatus === "Approved";
    return {
      walletId: `w-${user}`,
      walletAddress: person.walletAddress,
      kycStatus: person.kycStatus,
      identityReady: person.identityReady,
      kycReady: true,
      walletReady: approved,
      ready: approved && person.identityReady,
      unavailableReason: person.identityReady ? null : "Identity checks are offline on this node.",
      reviewDueAt: person.reviewDueAt,
    };
  };

  const transport: AzoaMoneyTransport = {
    account: {
      capabilities: () => answer("account.capabilities", () => ({ ok: true, value: { ready: true, kycAvailable: true, unavailableReason: null } })),
      status: () => answer("account.status", () => (me().opened ? { ok: true, value: accountView() } : refuse("setup_required", "No account exists for you yet."))),
      ensure: () => answer("account.ensure", () => {
        me().opened = true;
        return { ok: true, value: accountView() };
      }),
      beginKyc: () => answer("account.beginKyc", () => {
        if (!me().opened) return refuse("setup_required", "No account exists for you yet.");
        if (me().kycStatus === "Unknown") me().kycStatus = "Pending";
        return { ok: true, value: { verificationUrl: `https://widget.identomat.com/?session_token=${user}`, expiresAt: null, instructions: null, developmentSimulation: false } };
      }),
      revokeConsent: async () => {
        calls.push(`account.revokeConsent(${user})`);
        me().consentRevoked = true;
        return { ok: true, value: { revokedAt: new Date().toISOString() } };
      },
    },
    checkout: {
      capabilities: () => answer("checkout.capabilities", () => ({ ok: true, value: { available: true, unavailableReason: null } })),
      begin: request => answer("checkout.begin", () => {
        if (voidedKeys.has(`${user}:${request.requestKey}`)) return refuse("definite", "This payment request was cancelled and nothing can be paid under it. Start a new payment.");
        const existing = [...intents.values()].find(intent => intent.owner === user && intent.requestKey === request.requestKey);
        if (existing) return { ok: true, value: { ...strip(existing), resumed: true } };
        const intentId = randomUUID();
        const intent = {
          intentId, owner: user, requestKey: request.requestKey, status: "awaiting_payment" as const, settlementState: "not_started" as const,
          paymentUrl: `https://checkout.stripe.com/c/pay/cs_test_${intentId}`, resumed: false, amountPaidMinorUnits: null, currency: "usd", expiresAt: null,
        };
        intents.set(intentId, intent);
        return { ok: true, value: strip(intent) };
      }),
      get: intentId => answer("checkout.get", () => {
        const intent = intents.get(intentId);
        return intent && intent.owner === user ? { ok: true, value: strip(intent) } : refuse("definite", "That payment could not be found.");
      }),
      // Every intent here is an open, unpaid page, so the node always voids it (expiring the page).
      void: requestKey => answer("checkout.void", () => {
        voidedKeys.add(`${user}:${requestKey}`);
        const intent = [...intents.values()].find(candidate => candidate.owner === user && candidate.requestKey === requestKey);
        if (intent) Object.assign(intent, { status: "expired", paymentUrl: null });
        return { ok: true, value: { outcome: "voided", intentId: intent?.intentId ?? null } };
      }),
    },
    cashOut: {
      availability: () => answer("cashOut.availability", () => ({
        ok: true,
        value: {
          available: true, accountReady: true, cashableMinorUnits: 100_00, onHoldMinorUnits: 0, availableFrom: null,
          minimumAmountMinorUnits: 1_00, maximumAmountMinorUnits: 500_00, currencyCode: "USD", statusMessage: "You can cash out now.",
        },
      })),
      destination: () => answer("cashOut.destination", () => ({
        ok: true,
        value: {
          available: true, accountOpened: false, readyToReceive: false, detailsSubmitted: false, outstandingRequirementCount: 2,
          setupUrl: null, setupUrlExpiresAt: null, statusMessage: "Set up where your money goes.",
        },
      })),
      // F-H8: the node refuses tenant-initiated payout setup.
      beginDestinationSetup: () => answer("cashOut.beginDestinationSetup", () => refuse("account_holder_required", "Payout account setup can only be done by the account holder.")),
      start: request => answer("cashOut.start", () => {
        let view = cashOuts.get(`${user}:${request.requestKey}`);
        if (!view) {
          view = {
            owner: user, id: randomUUID(), status: "Requested", statusMessage: "Getting your money ready to send.", amountMinorUnits: request.amountMinorUnits,
            currencyCode: "USD", requestedAt: new Date().toISOString(), paidAt: null, reversedAt: null,
          };
          cashOuts.set(`${user}:${request.requestKey}`, view);
        }
        const { owner: _owner, ...rest } = view;
        return { ok: true, value: rest };
      }),
      history: () => answer("cashOut.history", () => ({
        ok: true,
        value: [...cashOuts.values()].filter(row => row.owner === user).map(({ owner: _owner, ...rest }) => rest),
      })),
    },
    sale: {
      sellable: () => answer("sale.sellable", () => ({
        ok: true,
        value: { availableBaseUnits: "5000000", decimals: 6, unitName: "AZU", currencyCode: "USD", statusMessage: "You can sell up to 5 AZU." },
      })),
      quote: request => answer("sale.quote", () => {
        const quote: SaleQuoteView = {
          quoteId: randomUUID(), quantityBaseUnits: request.quantityBaseUnits,
          proceedsMinorUnits: Number(BigInt(request.quantityBaseUnits) / 10_000n), currencyCode: "USD", expiresAt: null,
        };
        quotes.set(quote.quoteId, quote);
        return { ok: true, value: quote };
      }),
      sell: async request => {
        const result = await answer("sale.sell", (): MoneyResult<SaleView> => {
          const quote = quotes.get(request.quoteId);
          if (!quote) return refuse("definite", "That price expired. Get a new one.");
          let sale = sales.find(row => row.owner === user && row.requestKey === request.requestKey);
          if (!sale) {
            sale = {
              owner: user, requestKey: request.requestKey, quoteId: request.quoteId, id: randomUUID(), status: "accepted", statusMessage: "Sold. The money is on its way.",
              quantityBaseUnits: quote.quantityBaseUnits, proceedsMinorUnits: quote.proceedsMinorUnits, currencyCode: "USD", requestedAt: new Date().toISOString(),
            };
            sales.push(sale);
          }
          const { owner: _owner, requestKey: _key, quoteId: _quote, ...view } = sale;
          return { ok: true, value: view };
        });
        if (loseSale) {
          loseSale = false;
          throw new TypeError("fetch failed");
        }
        return result;
      },
    },
    balance: {
      get: () => answer("balance.get", () => ({ ok: true, value: { availableMinorUnits: 42_00, onHoldMinorUnits: 0, currencyCode: "USD", availableFrom: null } })),
    },
  };

  return {
    transport,
    signIn(next) {
      user = next;
    },
    person(name) {
      return records.get(name)!;
    },
    calls,
    sales,
    loseNextSale() {
      loseSale = true;
    },
  };
}

function strip(intent: CheckoutView & { owner?: string; requestKey?: string }): CheckoutView {
  const { owner: _owner, requestKey: _key, ...view } = intent;
  return view;
}
