import { afterEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "./client.js";

afterEach(() => vi.unstubAllGlobals());

it("uses authenticated operator paths and carries the recovery reason and known refund reference", async () => {
  const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ isError: false, result: {} }), { status: 200 }));
  vi.stubGlobal("fetch", fetch);
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "operator" });
  await client.recoverOnRampPayment("intent", "refund", "Verified original charge", "re_known");
  expect(fetch.mock.calls[0][0]).toBe("https://node.example/api/operator/on-ramp-lots/intents/intent/refund");
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({ reason: "Verified original charge", providerRefundId: "re_known" });
  await client.replayFiatEvent("event");
  expect(fetch.mock.calls[1][0]).toBe("https://node.example/api/operator/fiat-events/event/replay");
  expect(fetch.mock.calls[1][1].method).toBe("POST");
});
