import { afterEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "./client.js";

const operationId = "0123456789abcdef0123456789abcdef";

afterEach(() => vi.unstubAllGlobals());

it("preserves pending lifecycle facts and stable mutation keys", async () => {
  const pending = { operation: "Mint", operationId, nftId: "00000000-0000-0000-0000-000000000001",
    state: "Submitted", stage: "Create", assetId: null, transactionId: "tx", detail: "Awaiting chain confirmation" };
  const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ isError: false, result: pending }), { status: 202 }));
  vi.stubGlobal("fetch", fetch);
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "user" });
  const result = await client.mintUserNFT({ chainType: "Algorand", name: "Badge", metadataURI: "ipfs://metadata" }, "stable-key");
  expect(result).toMatchObject({ ok: true, value: { state: "Submitted", transactionId: "tx" } });
  expect(new Headers(fetch.mock.calls[0][1].headers).get("Idempotency-Key")).toBe("stable-key");
  await client.transferUserNFT(pending.nftId, "recipient", "transfer-key");
  expect(JSON.parse(fetch.mock.calls[1][1].body)).toEqual({ recipientAddress: "recipient" });
  await client.burnUserNFT(pending.nftId, "burn-key");
  expect(fetch.mock.calls[2][1].method).toBe("DELETE");
  for (const action of ["refresh", "accept", "cancel"] as const) {
    const acceptance = { requestId: "exact-request", expectedVersion: 3, state: "AwaitingRecipientAcceptance" as const, stage: "Create", generation: 0 };
    await ({ refresh: () => client.refreshNftLifecycle(operationId), accept: () => client.acceptNftLifecycle(operationId, acceptance),
      cancel: () => client.cancelNftLifecycle(operationId) })[action]();
    expect(fetch.mock.calls.at(-1)![0]).toBe(`https://node.example/api/usernft/lifecycle/${operationId}/${action}`);
    if (action === "accept") expect(JSON.parse(fetch.mock.calls.at(-1)![1].body)).toEqual(acceptance);
  }
});

it("retains a negative verification result instead of inventing an approval", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ isError: false, result: false }))));
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "user" });
  expect(await client.verifyNFTOwnership({ chainType: "Algorand", nftContractAddress: "", tokenId: "123" })).toEqual({ ok: true, value: false });
});

it("supports credential issuance and durable lookup with separate read/mutate contracts", async () => {
  const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ isError: false, result: {} })));
  vi.stubGlobal("fetch", fetch);
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "user" });
  await client.issueCredential("recipient", { chainType: "Algorand", name: "Credential", metadataURI: "ipfs://metadata" }, "issue-key");
  expect(new Headers(fetch.mock.calls[0][1].headers).get("Idempotency-Key")).toBe("issue-key");
  await client.getNftLifecycle(operationId);
  expect(fetch.mock.calls[1][1].method).toBe("GET");
});

it("encodes discovery cursors and exact base-unit fee authorization", async () => {
  const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ isError: false, result: {} })));
  vi.stubGlobal("fetch", fetch);
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "user" });
  await client.listNftLifecycles("cursor+/=", 10);
  expect(fetch.mock.calls[0][0]).toContain("cursor=cursor%2B%2F%3D");
  await client.burnUserNFT("00000000-0000-0000-0000-000000000001", "burn", { revision: 4, quoteId: "quote-four", maxNativeBaseUnits: "9007199254740993" });
  expect(fetch.mock.calls[1][0]).toContain("feeRevision=4&maxNativeFeeBaseUnits=9007199254740993");
  await client.refundNftTransfer(operationId, "refund", { revision: 4, quoteId: "quote-four", maxNativeBaseUnits: "1" });
  expect(JSON.parse(fetch.mock.calls[2][1].body)).toEqual({ feeAuthorization: { revision: 4, quoteId: "quote-four", maxNativeBaseUnits: "1" } });
});

it("preserves new pending revocation lifecycle evidence and request cancellation bodies", async () => {
  const fetch = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ isError: false, result: { status: "PendingConfirmation", isConfirmed: false, lifecycleId: "durable" } }), { status: 202 }));
  vi.stubGlobal("fetch", fetch);
  const client = new AzoaApiClient({ baseUrl: "https://node.example", token: "user" });
  expect(await client.revokeCredential("asset", { chainType: "Algorand" }, { idempotencyKey: "revoke" })).toMatchObject({ ok: true, value: { isConfirmed: false, lifecycleId: "durable" } });
  await client.cancelNftRequest("Mint", "lost-response-key");
  expect(JSON.parse(fetch.mock.calls[1][1].body)).toEqual({ operation: "Mint", requestKey: "lost-response-key" });
});
