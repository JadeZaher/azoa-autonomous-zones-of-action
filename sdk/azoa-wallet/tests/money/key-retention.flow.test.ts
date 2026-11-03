import { describe, expect, it } from "vitest";
import { describeRefusal, toSaleQuoteView, toBalanceView, createKeyStore, memoryStorage, MONEY_SCOPES, readSavedRequest, sendCashOut, sendSale, startCashOut, startSale } from "../../src/money/index.js";
import type { AzoaMoneyTransport, MoneyRefusalCode, SaleQuoteView } from "../../src/money/index.js";

const gates: MoneyRefusalCode[] = ["consent_revoked", "setup_required", "definite", "payments_not_configured", "unauthenticated"];
const quote: SaleQuoteView = { quoteId: "quote-pinned", quantityBaseUnits: "25", proceedsMinorUnits: 500, currencyCode: "USD", expiresAt: null };

function fixture() {
  const requests: unknown[] = [];
  let gate: MoneyRefusalCode | null = null;
  const send = async (request: unknown) => {
    requests.push(request);
    if (gate) return { ok: false as const, refusal: { code: gate, message: "Access currently unavailable." } };
    throw new Error("The accepted reply was lost.");
  };
  const transport = { cashOut: { start: send }, sale: { sell: send } } as unknown as AzoaMoneyTransport;
  const storage = memoryStorage();
  const store = () => createKeyStore({ subject: "member-a", storage });
  return { transport, requests, store, setGate: (next: MoneyRefusalCode) => { gate = next; } };
}

describe("a gate refusal does not settle a prior possibly-sent request", () => {
  it.each(gates)("cash-out keeps exact amount and key after reload then %s", async gate => {
    const f = fixture();
    await startCashOut(f.transport, f.store(), 2500);
    const saved = readSavedRequest(f.store(), MONEY_SCOPES.cashOut)!;
    f.setGate(gate);
    const result = await sendCashOut(f.transport, f.store(), saved);
    expect(result).toMatchObject({ kind: "refused", kept: saved });
    expect(readSavedRequest(f.store(), MONEY_SCOPES.cashOut)).toEqual(saved);
    expect(f.requests).toEqual([{ requestKey: saved.requestKey, amountMinorUnits: 2500 }, { requestKey: saved.requestKey, amountMinorUnits: 2500 }]);
    expect(readSavedRequest(createKeyStore({ subject: "member-b", storage: memoryStorage() }), MONEY_SCOPES.cashOut)).toBeNull();
  });

  it.each(gates)("sale retains the exact quote and key after %s", async gate => {
    const f = fixture();
    await startSale(f.transport, f.store(), quote);
    const saved = readSavedRequest(f.store(), MONEY_SCOPES.sale)!;
    f.setGate(gate);
    await sendSale(f.transport, f.store(), saved);
    expect(readSavedRequest(f.store(), MONEY_SCOPES.sale)).toEqual(saved);
    expect(f.requests).toEqual([{ requestKey: saved.requestKey, quoteId: quote.quoteId }, { requestKey: saved.requestKey, quoteId: quote.quoteId }]);
  });
});


describe("sale denomination intake", () => {
  it("preserves explicit precision and rejects missing or invalid custom denomination precision", () => {
    expect(toSaleQuoteView({ ...quote, currencyCode: "ARDA", currencyDecimals: 2 })).toMatchObject({ currencyCode: "ARDA", currencyDecimals: 2 });
    expect(() => toSaleQuoteView({ ...quote, currencyCode: "ARDA" })).toThrow();
    expect(() => toSaleQuoteView({ ...quote, currencyDecimals: 19 })).toThrow();
    expect(() => toBalanceView({ availableMinorUnits: 1, onHoldMinorUnits: 0, availableFrom: null, currencyCode: "ARDA", currencyDecimals: -1 })).toThrow();
  });
  it("saves the sale denomination with the exact quote for a later pending notice", async () => {
    const f = fixture();
    await startSale(f.transport, f.store(), { ...quote, currencyCode: "ARDA", currencyDecimals: 2 });
    expect(readSavedRequest(f.store(), MONEY_SCOPES.sale)).toMatchObject({ currencyCode: "ARDA", currencyDecimals: 2, reference: quote.quoteId });
  });
});


describe("strict storage admission", () => {
  it.each(["read fails once", "corrupt JSON"])("does not replace a prior key when %s", async mode => {
    const f = fixture();
    await startCashOut(f.transport, f.store(), 2500);
    const store = f.store();
    const original = store.read(MONEY_SCOPES.cashOut)!;
    if (mode === "corrupt JSON") store.write(MONEY_SCOPES.cashOut, "corrupt:" + original);
    let reads = 0;
    let writes = 0;
    const guarded = { read: (scope: string) => { if (mode === "read fails once" && reads++ === 0) throw new Error("Storage unavailable"); return store.read(scope); }, write: (scope: string, value: string) => { writes++; store.write(scope, value); }, remove: store.remove };
    const sentBefore = f.requests.length;
    const result = await startCashOut(f.transport, guarded, 5000);
    expect(result.kind).toBe("refused");
    expect(f.requests).toHaveLength(sentBefore);
    expect(writes).toBe(0);
    expect(store.read(MONEY_SCOPES.cashOut)).toBe(mode === "corrupt JSON" ? "corrupt:" + original : original);
  });
});


describe("a gate refusal never denies an earlier payment", () => {
  it.each(gates)("%s copy is neutral about prior effects", code => {
    expect(describeRefusal({ code, message: "Access refused." })).not.toMatch(/nothing was (?:moved|charged|sent)|nothing can be charged/iu);
  });
});
