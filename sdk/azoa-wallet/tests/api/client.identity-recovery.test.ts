import { beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);

describe("identity proof and operator recovery contracts", () => {
  let client: AzoaApiClient;
  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockImplementation(() => Promise.resolve({ ok: true, status: 200,
      json: () => Promise.resolve({ isError: false, result: { status: "AWAITING_OUTCOME" } }) }));
    client = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "session-token", apiKey: "tenant-key" });
  });

  it("keeps Solana identity casing and exact proof bytes while anonymous authentication omits credentials", async () => {
    const proof = { address: "CaseSensitiveBase58", chainType: "solana" as const, signature: "signed-proof", message: "Exact\nserver\nmessage" };
    await client.createWalletAuthenticationChallenge(proof.address, proof.chainType);
    await client.verifyWalletAuthentication(proof);
    const request = fetchMock.mock.calls[1][1] as RequestInit;
    expect(JSON.parse(request.body as string)).toEqual(proof);
    expect(request.headers).not.toHaveProperty("Authorization");
    expect(request.headers).not.toHaveProperty("X-API-KEY");
    expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/user/auth/verify");
  });

  it("passes reviewed evidence references without manufacturing a provider decision", async () => {
    const request = { reason: "Reviewed original vendor session", operatorReviewedSubjectAndProvider: true as const,
      confirmedProviderAbsence: false, providerSessionId: "vendor/reference" };
    await client.reconcileHostedSession("saved-attempt", request);
    expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/operator/hosted-sessions/saved-attempt/reconcile");
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(request);
    await client.refreshHostedSession("saved-attempt");
    expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({});
  });

  it("lists and replays the durable Stripe inbox identity", async () => {
    await client.listFiatEvents(500);
    expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/operator/fiat-events?limit=100");
    await client.replayFiatEvent("event/id");
    expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/operator/fiat-events/event%2Fid/replay");
  });
});
