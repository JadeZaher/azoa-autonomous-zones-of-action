// First-party `AzoaMoneyTransport` over an `AzoaApiClient` holding the person's own session. See `src/money/AGENTS.md` §direct-transport.
import type { AzoaApiClient, TenantKycStatus, WalletResult } from "../api/client.js";
import type { KycStatus } from "../api/identity.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { Result } from "../core/result.js";
import { moneyRefusal, refusalFromSdkError } from "./refusal.js";
import type { AzoaMoneyTransport, CustodialAccountView, MoneyResult } from "./types.js";
import {
  toCashOutQuoteView,
  toCashOutView,
  toCheckoutCapabilitiesView,
  toCheckoutView,
  toCheckoutVoidView,
  toKycSessionView,
  toPayoutDestinationView,
} from "./views.js";

export interface DirectMoneyTransportOptions {
  /** Where the payment provider returns the person; must be on an origin the node allowlists. */
  checkoutReturnUrl?: string;
  /** Allowlisted checkout routing value; defaults to `funds-top-up`. */
  checkoutPurpose?: string;
  /** How many cash-outs `history()` reads; defaults to 20. */
  historyLimit?: number;
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** First-party KYC words → the custodial vocabulary the gate reads. */
const KYC_STATUS: Record<KycStatus, TenantKycStatus> = {
  PENDING: "Pending",
  IN_REVIEW: "UnderReview",
  APPROVED: "Approved",
  REJECTED: "Rejected",
  EXPIRED: "Unknown",
};

/** Builds the transport AZOA's own apps use; refuses a client carrying an API key, which must never reach a browser. */
export function createDirectMoneyTransport(client: AzoaApiClient, userId: string, options: DirectMoneyTransportOptions = {}): AzoaMoneyTransport {
  if (client.hasApiKey()) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      "createDirectMoneyTransport refuses an AzoaApiClient configured with an apiKey: a tenant credential must never reach a browser. Implement AzoaMoneyTransport over your own backend instead.",
    );
  }
  if (!UUID_RE.test(userId)) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "createDirectMoneyTransport needs the signed-in person's AZOA user id.");
  }
  const returnUrl = options.checkoutReturnUrl;

  const accountStatus = async (): Promise<MoneyResult<CustodialAccountView>> => {
    const [kyc, wallets] = await Promise.all([settle(() => client.getKycStatus()), settle(() => client.listWallets())]);
    let kycStatus: TenantKycStatus = "Unknown";
    if (kyc.ok) kycStatus = (kyc.value ? KYC_STATUS[kyc.value.status] : undefined) ?? "Unknown";
    else if (kyc.error.status !== 404) return { ok: false, refusal: refusalFromSdkError(kyc.error) };
    // The wallet is informational for a first-party login: checkout gates on KYC approval alone.
    const wallet = wallets.ok ? pickWallet(wallets.value) : null;
    const approved = kycStatus === "Approved";
    return {
      ok: true,
      value: {
        walletId: wallet?.id ?? null,
        walletAddress: wallet?.address ?? null,
        kycStatus,
        identityReady: true,
        kycReady: true,
        walletReady: wallet !== null,
        ready: approved,
        unavailableReason: approved ? null : kycStatus === "UnderReview" ? "An additional compliance check is under review." : "KYC verification is not approved.",
        reviewDueAt: null,
      },
    };
  };

  return {
    account: {
      capabilities: async () => ({ ok: true, value: { ready: true, kycAvailable: true, unavailableReason: null } }),
      status: accountStatus,
      // A first-party login is its own account; there is nothing to create.
      ensure: accountStatus,
      beginKyc: () => call(() => client.startKycSession(), toKycSessionView),
      revokeConsent: async () => ({
        ok: false,
        refusal: moneyRefusal("definite", "A first-party session acts for itself, so there is no app permission to withdraw here."),
      }),
    },
    checkout: {
      capabilities: () => call(() => client.getFiatCheckoutCapabilities(), toCheckoutCapabilitiesView),
      begin: request => call(() => client.startFiatCheckout(userId, {
        requestKey: request.requestKey,
        amountMinorUnits: request.amountMinorUnits,
        purpose: options.checkoutPurpose ?? "funds-top-up",
        ...(returnUrl ? { successUrl: returnUrl, cancelUrl: returnUrl } : {}),
      }), toCheckoutView),
      get: intentId => call(() => client.getFiatCheckoutIntent(userId, intentId), toCheckoutView),
      void: requestKey => call(() => client.voidFiatCheckoutRequest(userId, requestKey), toCheckoutVoidView),
    },
    cashOut: {
      availability: () => call(() => client.getCashOutAvailability(userId), toCashOutQuoteView),
      destination: () => call(() => client.getPayoutAccountStatus(userId), toPayoutDestinationView),
      beginDestinationSetup: () => call(() => client.beginPayoutAccountSetup(userId), toPayoutDestinationView),
      start: request => call(() => client.startCashOut(userId, { requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }), toCashOutView),
      history: () => call(() => client.listCashOutHistory(userId, options.historyLimit ?? 20), rows => (Array.isArray(rows) ? rows.map(toCashOutView) : [])),
    },
    balance: {
      get: () => call(() => client.getCashOutAvailability(userId), quote => ({
        availableMinorUnits: quote.cashableMinorUnits,
        onHoldMinorUnits: quote.onHoldMinorUnits,
        currencyCode: quote.currencyCode,
        availableFrom: quote.availableFrom ?? null,
      })),
    },
  };
}

/** Runs one client call and narrows its value; never rejects: a throwing guard or an unreadable body becomes a refusal. */
async function call<T, V>(run: () => Promise<Result<T, SdkError>>, narrow: (value: T) => V): Promise<MoneyResult<V>> {
  const result = await settle(run);
  if (!result.ok) return { ok: false, refusal: refusalFromSdkError(result.error) };
  try {
    return { ok: true, value: narrow(result.value) };
  } catch {
    // A 200 whose body is not the view (null, a bare string): not a definite answer, so a saved key survives it.
    return { ok: false, refusal: moneyRefusal("unconfirmed") };
  }
}

async function settle<T>(run: () => Promise<Result<T, SdkError>>): Promise<Result<T, SdkError>> {
  try {
    return await run();
  } catch (error) {
    return {
      ok: false,
      error: error instanceof SdkError ? error : new SdkError(SdkErrorCode.UNKNOWN, "The request could not be made.", { cause: error as Error }),
    };
  }
}

function pickWallet(wallets: WalletResult[] | undefined): WalletResult | null {
  if (!Array.isArray(wallets) || wallets.length === 0) return null;
  return wallets.find(wallet => wallet.isDefault) ?? wallets[0] ?? null;
}
