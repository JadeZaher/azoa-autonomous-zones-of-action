// Reusable, framework-agnostic contract every `AzoaMoneyTransport` must pass. See `src/money/AGENTS.md` §contract.
import { isMoneyRefusalCode, releasesRequestKey } from "../refusal.js";
import { newRequestKey } from "../request-key.js";
import type { AzoaMoneyTransport, MoneyResult } from "../types.js";
import { findForbiddenViewKeys } from "../views.js";

/** Builds a transport over a fresh fixture; called once per case. */
export type MoneyTransportFactory = () => AzoaMoneyTransport | Promise<AzoaMoneyTransport>;

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
        const transport = await factory();
        for (const [label, read] of readsOf(transport)) await resolved(label, read());
        return "passed";
      },
    },
    {
      name: "no view carries subject or tenant identifiers",
      async run() {
        const transport = await factory();
        for (const [label, result] of await callAll(transport, amount, options.cashOutAmountMinorUnits)) {
          if (!result.ok) continue;
          const leaked = findForbiddenViewKeys(result.value);
          check(leaked.length === 0, `${label} leaked ${leaked.join(", ")}`);
        }
        return "passed";
      },
    },
    {
      name: "checkout.begin resumes the same intent for the same request key",
      async run() {
        const transport = await factory();
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
        const transport = await factory();
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
        const transport = await factory();
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
        const transport = await factory();
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
        const transport = await factory();
        for (const [label, result] of await callAll(transport, amount, options.cashOutAmountMinorUnits)) {
          if (!result.ok) continue;
          for (const [path, value] of amountFields(result.value)) {
            check(typeof value === "number" ? Number.isSafeInteger(value) : value === null, `${label} ${path} must be an integer number of minor units`);
          }
          for (const [path, value] of baseUnitFields(result.value)) check(typeof value === "string" && /^\d+$/.test(value), `${label} ${path} must be a base-unit decimal string`);
        }
        return "passed";
      },
    },
  ];
  return cases;
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

/** Every read, then every action a person can take (consent withdrawn last, so it cannot gate the others). */
async function callAll(transport: AzoaMoneyTransport, amount: number, cashOutAmount?: number): Promise<Labelled[]> {
  const results = await readAll(transport);
  const record = async (label: string, pending: Promise<MoneyResult<unknown>>) => {
    const result = await resolved(label, pending);
    results.push([label, result]);
    return result;
  };
  await record("checkout.begin", transport.checkout.begin({ requestKey: newRequestKey(), amountMinorUnits: amount }));
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
