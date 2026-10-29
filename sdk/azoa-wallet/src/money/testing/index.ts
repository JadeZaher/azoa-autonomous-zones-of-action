// Reusable, framework-agnostic contract every `AzoaMoneyTransport` must pass. See `src/money/AGENTS.md` §contract.
import { isOpenCheckout, isSettledCheckout } from "../presentation.js";
import { isMoneyRefusalCode, releasesRequestKey } from "../refusal.js";
import { newRequestKey } from "../request-key.js";
import type { AzoaMoneyTransport, MoneyResult } from "../types.js";
import { findForbiddenViewKeys } from "../views.js";

/** Payment-provider controls the void cases need, wired to the fixture's fake node or sandbox. */
export interface CheckoutFixtureControls {
  /** The person paid this checkout at the provider (the webhook need not have arrived). */
  markPaid(intentId: string): void | Promise<void>;
  /** The provider can no longer be read for this checkout. */
  makeUnreadable(intentId: string): void | Promise<void>;
}

/** A transport plus the controls some cases need; without `checkout`, those cases skip. */
export interface MoneyTransportFixture {
  transport: AzoaMoneyTransport;
  checkout?: CheckoutFixtureControls;
}

/** Builds a transport (or a transport with fixture controls) over a fresh fixture; called once per case. */
export type MoneyTransportFactory = () => AzoaMoneyTransport | MoneyTransportFixture | Promise<AzoaMoneyTransport | MoneyTransportFixture>;

export interface MoneyTransportContractOptions {
  /** Amount for keyed checkouts; defaults to 2500 (25.00). */
  amountMinorUnits?: number;
  /** Amount for keyed cash-outs; defaults to the availability minimum. */
  cashOutAmountMinorUnits?: number;
  /** Count a skipped case as a failure (the fixture must reach every case). */
  requireAll?: boolean;
}

/** One case: resolves `"passed"` or `{ skipped }`, rejects with the violated rule. */
export interface MoneyTransportContractCase {
  name: string;
  run(): Promise<"passed" | { skipped: string }>;
}

export interface MoneyTransportContractReport {
  ok: boolean;
  passed: string[];
  skipped: { name: string; reason: string }[];
  failed: { name: string; reason: string }[];
}

/** Thrown by a case when the transport broke a rule. */
export class MoneyContractViolation extends Error {
  constructor(message: string) {
    super(message);
    this.name = "MoneyContractViolation";
  }
}

/** The contract as separate cases, for registering one test per case in any runner. */
export function moneyTransportContractCases(factory: MoneyTransportFactory, options: MoneyTransportContractOptions = {}): MoneyTransportContractCase[] {
  const amount = options.amountMinorUnits ?? 25_00;
  // Each case builds its own fixture: the forbidden-key and amount cases call every action, consent withdrawal included.
  const cases: MoneyTransportContractCase[] = [
    {
      name: "every read resolves to a MoneyResult with a known refusal code, never a rejection",
      async run() {
        const { transport } = await build(factory);
        for (const [label, read] of readsOf(transport)) await resolved(label, read());
        return "passed";
      },
    },
    {
      name: "no view carries subject or tenant identifiers",
      async run() {
        const fixture = await build(factory);
        for (const [label, result] of await callAll(fixture, amount, options.cashOutAmountMinorUnits)) {
          // Refusals too: a same-key 409's `serverResult` reaches the page like any view.
          const leaked = findForbiddenViewKeys(result.ok ? result.value : result.refusal);
          check(leaked.length === 0, `${label} leaked ${leaked.join(", ")}`);
        }
        return "passed";
      },
    },
    {
      name: "checkout.begin resumes the same intent for the same request key",
      async run() {
        const { transport } = await build(factory);
        const request = { requestKey: newRequestKey(), amountMinorUnits: amount };
        const first = await resolved("checkout.begin", transport.checkout.begin(request));
        if (!first.ok) return { skipped: `checkout.begin refused (${first.refusal.code}); the fixture must accept a checkout` };
        const again = await resolved("checkout.begin (same key)", transport.checkout.begin(request));
        check(again.ok, "checkout.begin with the same key was refused after the first was admitted");
        check(again.value.intentId === first.value.intentId, "the same request key opened a second checkout");
        check(again.value.resumed === true, "a same-key checkout.begin must report resumed: true");
        const read = await resolved("checkout.get", transport.checkout.get(first.value.intentId));
        check(read.ok && read.value.intentId === first.value.intentId, "checkout.get could not read the intent checkout.begin returned");
        return "passed";
      },
    },
    {
      name: "a new request key opens a new checkout",
      async run() {
        const { transport } = await build(factory);
        const first = await resolved("checkout.begin", transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: amount }));
        if (!first.ok) return { skipped: `checkout.begin refused (${first.refusal.code}); the fixture must accept a checkout` };
        const second = await resolved("checkout.begin", transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: amount }));
        check(second.ok && second.value.intentId !== first.value.intentId, "two request keys must open two checkouts");
        return "passed";
      },
    },
    {
      name: "checkout.get on an unknown or malformed intent is a definite refusal",
      async run() {
        const { transport } = await build(factory);
        for (const intentId of [newRequestKey(), "not-an-intent"]) {
          const result = await resolved(`checkout.get(${intentId})`, transport.checkout.get(intentId));
          check(!result.ok, `checkout.get(${intentId}) answered an intent that does not exist`);
          if (!result.ok) check(releasesRequestKey(result.refusal), `checkout.get(${intentId}) must refuse definitely, not ${result.refusal.code}, or a poller never stops`);
        }
        return "passed";
      },
    },
    {
      name: "cashOut.start pays once per request key and history lists it once",
      async run() {
        const { transport } = await build(factory);
        const quote = await resolved("cashOut.availability", transport.cashOut.availability());
        if (!quote.ok) return { skipped: `cashOut.availability refused (${quote.refusal.code})` };
        const cashOutAmount = options.cashOutAmountMinorUnits ?? quote.value.minimumAmountMinorUnits;
        if (!quote.value.accountReady || quote.value.cashableMinorUnits < cashOutAmount) return { skipped: "the fixture has nothing cashable to a ready payout account" };
        const request = { requestKey: newRequestKey(), amountMinorUnits: cashOutAmount };
        const first = await resolved("cashOut.start", transport.cashOut.start(request));
        if (!first.ok) return { skipped: `cashOut.start refused (${first.refusal.code})` };
        const again = await resolved("cashOut.start (same key)", transport.cashOut.start(request));
        check(again.ok && again.value.id === first.value.id, "the same request key started a second cash-out");
        const history = await resolved("cashOut.history", transport.cashOut.history());
        check(history.ok, "cashOut.history was refused after a cash-out was admitted");
        check(history.value.filter(row => row.id === first.value.id).length === 1, "history must list the keyed cash-out exactly once");
        return "passed";
      },
    },
    {
      name: "money amounts are integer minor units and unit quantities are base-unit strings",
      async run() {
        const fixture = await build(factory);
        for (const [label, result] of await callAll(fixture, amount, options.cashOutAmountMinorUnits)) {
          if (!result.ok) continue;
          for (const [path, value] of amountFields(result.value)) {
            check(typeof value === "number" ? Number.isSafeInteger(value) : value === null, `${label} ${path} must be an integer number of minor units`);
          }
          for (const [path, value] of baseUnitFields(result.value)) check(typeof value === "string" && /^\d+$/.test(value), `${label} ${path} must be a base-unit decimal string`);
        }
        return "passed";
      },
    },
    {
      name: "checkout.void on a key nothing was admitted under voids it for good, and that key never opens a checkout",
      async run() {
        const { transport } = await build(factory);
        const requestKey = newRequestKey();
        for (const label of ["checkout.void", "checkout.void (again)"]) {
          const voided = await resolved(label, transport.checkout.void(requestKey));
          check(voided.ok && voided.value.outcome === "voided" && voided.value.intentId === null, `${label} on an unused key must answer voided with no intent`);
        }
        const begun = await resolved("checkout.begin (voided key)", transport.checkout.begin({ requestKey, amountMinorUnits: amount }));
        check(!begun.ok, "checkout.begin opened a checkout under a voided key");
        check(releasesRequestKey(begun.refusal), `checkout.begin on a voided key must refuse definitely (REQUEST_VOIDED), not ${begun.refusal.code}`);
        return "passed";
      },
    },
    {
      name: "checkout.void on an open, unpaid checkout voids it (idempotently) and closes it",
      async run() {
        const { transport } = await build(factory);
        const request = { requestKey: newRequestKey(), amountMinorUnits: amount };
        const first = await resolved("checkout.begin", transport.checkout.begin(request));
        if (!first.ok) return { skipped: `checkout.begin refused (${first.refusal.code}); the fixture must accept a checkout` };
        for (const label of ["checkout.void", "checkout.void (again)"]) {
          const voided = await resolved(label, transport.checkout.void(request.requestKey));
          check(voided.ok && voided.value.outcome === "voided" && voided.value.intentId === first.value.intentId, `${label} on an open, unpaid checkout must answer voided naming its intent`);
        }
        const read = await resolved("checkout.get (voided)", transport.checkout.get(first.value.intentId));
        check(read.ok && isSettledCheckout(read.value) && !isOpenCheckout(read.value), "a voided checkout must read closed and unpaid");
        const resent = await resolved("checkout.begin (voided key)", transport.checkout.begin(request));
        check(!resent.ok && releasesRequestKey(resent.refusal), "checkout.begin on a voided key must refuse definitely (REQUEST_VOIDED)");
        return "passed";
      },
    },
    {
      name: "checkout.void on a paid checkout reveals it, narrowed, and never voids it",
      async run() {
        const { transport, checkout: controls } = await build(factory);
        if (!controls) return { skipped: "the fixture has no checkout controls (markPaid) to pay a checkout" };
        const request = { requestKey: newRequestKey(), amountMinorUnits: amount };
        const first = await resolved("checkout.begin", transport.checkout.begin(request));
        if (!first.ok) return { skipped: `checkout.begin refused (${first.refusal.code}); the fixture must accept a checkout` };
        await controls.markPaid(first.value.intentId);
        for (const label of ["checkout.void", "checkout.void (again)"]) {
          const revealed = await resolved(label, transport.checkout.void(request.requestKey));
          check(revealed.ok && revealed.value.outcome === "revealed", `${label} on a paid checkout must reveal it, never void it`);
          check(revealed.value.intent.intentId === first.value.intentId, `${label} revealed a different checkout`);
          const leaked = findForbiddenViewKeys(revealed.value);
          check(leaked.length === 0, `${label} leaked ${leaked.join(", ")}`);
        }
        const resumed = await resolved("checkout.begin (revealed key)", transport.checkout.begin(request));
        check(resumed.ok && resumed.value.intentId === first.value.intentId, "a revealed key must still resume its own checkout");
        return "passed";
      },
    },
    {
      name: "checkout.void that cannot read the provider refuses with a code that keeps the key",
      async run() {
        const { transport, checkout: controls } = await build(factory);
        if (!controls) return { skipped: "the fixture has no checkout controls (makeUnreadable) to cut the provider off" };
        const request = { requestKey: newRequestKey(), amountMinorUnits: amount };
        const first = await resolved("checkout.begin", transport.checkout.begin(request));
        if (!first.ok) return { skipped: `checkout.begin refused (${first.refusal.code}); the fixture must accept a checkout` };
        await controls.makeUnreadable(first.value.intentId);
        const result = await resolved("checkout.void", transport.checkout.void(request.requestKey));
        check(!result.ok, "checkout.void decided a checkout nobody could read; it must refuse");
        check(!releasesRequestKey(result.refusal), `a void that could not decide must keep the key, not refuse with ${result.refusal.code}`);
        return "passed";
      },
    },
  ];
  return cases;
}

/** The factory's transport and controls, whichever shape it returned. */
async function build(factory: MoneyTransportFactory): Promise<MoneyTransportFixture> {
  const built = await factory();
  return "transport" in built ? built : { transport: built };
}

/** Runs every case and reports; `ok` is false on any failure (or any skip with `requireAll`). */
export async function runMoneyTransportContract(factory: MoneyTransportFactory, options: MoneyTransportContractOptions = {}): Promise<MoneyTransportContractReport> {
  const report: MoneyTransportContractReport = { ok: true, passed: [], skipped: [], failed: [] };
  for (const contractCase of moneyTransportContractCases(factory, options)) {
    try {
      const outcome = await contractCase.run();
      if (outcome === "passed") report.passed.push(contractCase.name);
      else report.skipped.push({ name: contractCase.name, reason: outcome.skipped });
    } catch (error) {
      report.failed.push({ name: contractCase.name, reason: error instanceof Error ? error.message : String(error) });
    }
  }
  report.ok = report.failed.length === 0 && (!options.requireAll || report.skipped.length === 0);
  return report;
}

type Read = readonly [string, () => Promise<MoneyResult<unknown>>];

function readsOf(transport: AzoaMoneyTransport): Read[] {
  const reads: Read[] = [
    ["account.capabilities", () => transport.account.capabilities()],
    ["account.status", () => transport.account.status()],
    ["checkout.capabilities", () => transport.checkout.capabilities()],
    ["checkout.get(unknown)", () => transport.checkout.get(newRequestKey())],
    ["cashOut.availability", () => transport.cashOut.availability()],
    ["cashOut.destination", () => transport.cashOut.destination()],
    ["cashOut.history", () => transport.cashOut.history()],
    ["balance.get", () => transport.balance.get()],
  ];
  const sale = transport.sale;
  if (sale) reads.push(["sale.sellable", () => sale.sellable()]);
  return reads;
}

type Labelled = readonly [string, MoneyResult<unknown>];

async function readAll(transport: AzoaMoneyTransport): Promise<Labelled[]> {
  const results: Labelled[] = [];
  for (const [label, read] of readsOf(transport)) results.push([label, await resolved(label, read())]);
  return results;
}

/**
 * Every read, then every action a person can take (consent withdrawn last, so it cannot gate the others): a begun
 * checkout read back by id, and (with controls) a same-key resend after `markPaid`, whose answer may be a final 409
 * carrying the intent in `serverResult`.
 */
async function callAll({ transport, checkout: controls }: MoneyTransportFixture, amount: number, cashOutAmount?: number): Promise<Labelled[]> {
  const results = await readAll(transport);
  const record = async <T>(label: string, pending: Promise<MoneyResult<T>>) => {
    const result = await resolved(label, pending);
    results.push([label, result]);
    return result;
  };
  const checkoutKey = newRequestKey();
  const begun = await record("checkout.begin", transport.checkout.begin({ requestKey: checkoutKey, amountMinorUnits: amount }));
  if (begun.ok) await record(`checkout.get(${begun.value.intentId})`, transport.checkout.get(begun.value.intentId));
  await record("checkout.void", transport.checkout.void(checkoutKey));
  if (controls) {
    const paidRequest = { requestKey: newRequestKey(), amountMinorUnits: amount };
    const paid = await record("checkout.begin (to pay)", transport.checkout.begin(paidRequest));
    if (paid.ok) {
      await controls.markPaid(paid.value.intentId);
      await record("checkout.begin (same key, after markPaid)", transport.checkout.begin(paidRequest));
    }
  }
  await record("account.ensure", transport.account.ensure());
  await record("account.beginKyc", transport.account.beginKyc());
  await record("cashOut.beginDestinationSetup", transport.cashOut.beginDestinationSetup());
  const quote = await resolved("cashOut.availability", transport.cashOut.availability());
  if (quote.ok) {
    const amountToCashOut = cashOutAmount ?? quote.value.minimumAmountMinorUnits;
    await record("cashOut.start", transport.cashOut.start({ requestKey: newRequestKey(), amountMinorUnits: amountToCashOut }));
  }
  const sale = transport.sale;
  if (sale) {
    const sellable = await resolved("sale.sellable", sale.sellable());
    const quantity = sellable.ok && /^[1-9]\d*$/.test(sellable.value.availableBaseUnits) ? "1" : "0";
    const saleQuote = await record("sale.quote", sale.quote({ quantityBaseUnits: quantity }));
    if (saleQuote.ok) {
      const { quoteId } = saleQuote.value as { quoteId: string };
      await record("sale.sell", sale.sell({ requestKey: newRequestKey(), quoteId }));
    }
  }
  await record("account.revokeConsent", transport.account.revokeConsent());
  return results;
}

/** Awaits one call and checks it is a well-formed `MoneyResult`. */
async function resolved<T>(label: string, pending: Promise<MoneyResult<T>>): Promise<MoneyResult<T>> {
  let result: MoneyResult<T>;
  try {
    result = await pending;
  } catch (error) {
    throw new MoneyContractViolation(`${label} rejected (${error instanceof Error ? error.message : String(error)}); a transport resolves refusals instead`);
  }
  check(result !== null && typeof result === "object" && typeof result.ok === "boolean", `${label} did not resolve to a MoneyResult`);
  if (result.ok) {
    check("value" in result, `${label} resolved ok without a value`);
  } else {
    const refusal = (result as { refusal?: unknown }).refusal as { code?: unknown; message?: unknown; retryAfterSeconds?: unknown } | undefined;
    check(isMoneyRefusalCode(refusal?.code), `${label} refused with unknown code ${String(refusal?.code)}`);
    check(typeof refusal?.message === "string" && refusal.message.trim() !== "", `${label} refused without a message for the person`);
    check(refusal?.retryAfterSeconds === undefined || (typeof refusal.retryAfterSeconds === "number" && refusal.retryAfterSeconds >= 0), `${label} retryAfterSeconds must be a non-negative number`);
  }
  return result;
}

function check(condition: boolean, message: string): asserts condition {
  if (!condition) throw new MoneyContractViolation(message);
}

/** Every `*MinorUnits` field anywhere in a view. */
function amountFields(value: unknown, path = ""): [string, unknown][] {
  return fieldsMatching(value, key => key.endsWith("MinorUnits"), path);
}

/** Every `*BaseUnits` field anywhere in a view. */
function baseUnitFields(value: unknown, path = ""): [string, unknown][] {
  return fieldsMatching(value, key => key.endsWith("BaseUnits"), path);
}

function fieldsMatching(value: unknown, matches: (key: string) => boolean, path: string): [string, unknown][] {
  if (value === null || typeof value !== "object") return [];
  if (Array.isArray(value)) return value.flatMap((item, index) => fieldsMatching(item, matches, `${path}[${index}]`));
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => {
    const here = path ? `${path}.${key}` : key;
    return [...(matches(key) ? [[here, child] as [string, unknown]] : []), ...fieldsMatching(child, matches, here)];
  });
}
