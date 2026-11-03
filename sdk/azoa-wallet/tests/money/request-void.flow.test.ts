import { afterEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { claimRequestKey, createDirectMoneyTransport, createKeyStore, discardCashOut, discardSale, memoryStorage, MONEY_SCOPES, readSavedRequest, startCashOut, startSale } from "../../src/money/index.js";
import type { AzoaMoneyTransport, SaleQuoteView } from "../../src/money/index.js";
import { CASH_OUT_START, createNodeEmulator } from "./node-emulator.js";

afterEach(() => vi.unstubAllGlobals());
describe("cash-out void is a server decision", () => {
  it("voids an absent key durably and a late start cannot admit a cash-out", async () => {
    const node = createNodeEmulator();
    vi.stubGlobal("fetch", node.fetch);
    const transport = createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
    const store = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    const claim = claimRequestKey(store, MONEY_SCOPES.cashOut, 2500);
    if (claim.kind !== "ready") throw new Error("Expected fresh request.");
    expect((await discardCashOut(transport, store, claim.request)).kind).toBe("discarded");
    expect(readSavedRequest(store, MONEY_SCOPES.cashOut)).toBeNull();
    expect((await transport.cashOut.start({ requestKey: claim.request.requestKey, amountMinorUnits: 2500 })).ok).toBe(false);
    expect(node.cashOuts).toHaveLength(0);
  });
  it("a lost accepted answer is revealed, never voided or sent a second time", async () => {
    const node = createNodeEmulator();
    vi.stubGlobal("fetch", node.fetch);
    const transport = createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
    const store = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    node.loseNextResponse(CASH_OUT_START);
    await startCashOut(transport, store, 2500);
    const saved = readSavedRequest(store, MONEY_SCOPES.cashOut)!;
    expect(await discardCashOut(transport, store, saved)).toMatchObject({ kind: "revealed", view: { id: node.cashOuts[0]!.id, amountMinorUnits: 2500 } });
    expect(node.cashOuts).toHaveLength(1);
  });
  it.each([null, {}, { outcome: "void" }, { outcome: "revealed", cashOut: { id: "not-a-record" } }])("unreadable void keeps the key: %j", async answer => {
    const node = createNodeEmulator();
    const direct = createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
    const transport = { ...direct, cashOut: { ...direct.cashOut, void: async () => ({ ok: true, value: answer }) } } as unknown as AzoaMoneyTransport;
    const store = createKeyStore({ subject: node.userId, storage: memoryStorage() });
    claimRequestKey(store, MONEY_SCOPES.cashOut, 2500);
    const saved = readSavedRequest(store, MONEY_SCOPES.cashOut)!;
    expect((await discardCashOut(transport, store, saved)).kind).toBe("refused");
    expect(readSavedRequest(store, MONEY_SCOPES.cashOut)).toEqual(saved);
  });
});

describe("quote-bound sale void", () => {
  const quote: SaleQuoteView = { quoteId: "pinned-quote", quantityBaseUnits: "3", proceedsMinorUnits: 1234, currencyCode: "ARDA", currencyDecimals: 2, expiresAt: null };
  it("asks the host about the saved quote and clears only its explicit tombstone", async () => {
    const voidSale = vi.fn(async () => ({ ok: true as const, value: { outcome: "voided" as const } }));
    const transport = { sale: { sell: async () => { throw new Error("Lost response"); }, void: voidSale } } as unknown as AzoaMoneyTransport;
    const store = createKeyStore({ subject: "ada", storage: memoryStorage() });
    await startSale(transport, store, quote);
    const saved = readSavedRequest(store, MONEY_SCOPES.sale)!;
    expect((await discardSale(transport, store, saved)).kind).toBe("discarded");
    expect(voidSale).toHaveBeenCalledWith({ requestKey: saved.requestKey, quoteId: quote.quoteId });
    expect(readSavedRequest(store, MONEY_SCOPES.sale)).toBeNull();
  });
});
