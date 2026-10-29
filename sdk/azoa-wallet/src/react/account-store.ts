// One account read per provider, shared by every account hook (external store for useSyncExternalStore). See `src/react/AGENTS.md` §account-store.
import { deriveAccountGate, toConsentRevocationView, toCustodialAccountView, toKycSessionView } from "../money/index.js";
import type {
  AccountGateState,
  AzoaMoneyTransport,
  ConsentRevocationView,
  CustodialAccountView,
  KycSessionView,
  MoneyRefusal,
  MoneyResult,
} from "../money/index.js";
import { settleMoney } from "./settle.js";

export interface AccountSnapshot {
  gate: AccountGateState | "loading";
  account: CustodialAccountView | null;
  /** The refusal the last read or action produced. */
  refusal: MoneyRefusal | null;
  kycSession: KycSessionView | null;
  busy: boolean;
}

export class AccountStore {
  private snapshot: AccountSnapshot = { gate: "loading", account: null, refusal: null, kycSession: null, busy: false };
  private readonly listeners = new Set<() => void>();
  private reading: Promise<void> | null = null;
  private loaded = false;
  private inFlight = 0;

  constructor(private readonly transport: AzoaMoneyTransport) {}

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => void this.listeners.delete(listener);
  };

  getSnapshot = (): AccountSnapshot => this.snapshot;

  /** Reads the account once per store, however many hooks mount. */
  load = (): void => {
    if (!this.loaded) void this.refresh();
  };

  /** Re-reads the account; concurrent calls share one request. */
  refresh = (): Promise<void> => {
    this.loaded = true;
    this.reading ??= this.track(settleMoney(this.transport.account.status(), toCustodialAccountView)).then(result => this.applyRead(result)).finally(() => {
      this.reading = null;
    });
    return this.reading;
  };

  ensure = async (): Promise<MoneyResult<CustodialAccountView>> => {
    const result = await this.track(settleMoney(this.transport.account.ensure(), toCustodialAccountView));
    this.applyRead(result);
    return result;
  };

  /** Ensures the account exists, then opens (or resumes) identity verification. */
  beginKyc = async (): Promise<MoneyResult<KycSessionView>> => {
    const ensured = await this.ensure();
    if (!ensured.ok) return ensured;
    const result = await this.track(settleMoney(this.transport.account.beginKyc(), toKycSessionView));
    this.patch(result.ok ? { kycSession: result.value, refusal: null } : { refusal: result.refusal });
    return result;
  };

  revokeConsent = async (): Promise<MoneyResult<ConsentRevocationView>> => {
    const result = await this.track(settleMoney(this.transport.account.revokeConsent(), toConsentRevocationView));
    this.patch(result.ok ? { gate: "revoked", refusal: null } : { refusal: result.refusal });
    return result;
  };

  private applyRead(result: MoneyResult<CustodialAccountView>): void {
    this.patch({
      gate: deriveAccountGate(result),
      account: result.ok ? result.value : this.snapshot.account,
      refusal: result.ok ? null : result.refusal,
    });
  }

  private async track<T>(pending: Promise<T>): Promise<T> {
    this.inFlight += 1;
    this.patch({ busy: true });
    try {
      return await pending;
    } finally {
      this.inFlight -= 1;
      this.patch({ busy: this.inFlight > 0 });
    }
  }

  private patch(next: Partial<AccountSnapshot>): void {
    this.snapshot = { ...this.snapshot, ...next };
    for (const listener of this.listeners) listener();
  }
}
