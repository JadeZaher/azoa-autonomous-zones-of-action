import { beforeEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.ts";
import { isOk } from "../../src/core/result.js";

const scopeCatalog = [
  { scope: "dapp:develop", description: "DApp development", isSelfIssuable: true },
  { scope: "dapp:manage", description: "DApp management", isSelfIssuable: true },
  { scope: "wallet:manage", description: "Wallet management", isSelfIssuable: true },
];

function installFetchMock() {
  const fetchMock = vi.fn(async () =>
    new Response(JSON.stringify({ isError: false, result: scopeCatalog }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("AzoaApiClient scope discovery", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("exposes the clearer self-issuable scope helper", async () => {
    const fetchMock = installFetchMock();
    const client = new AzoaApiClient({ baseUrl: "http://localhost:5000" });

    const result = await client.listSelfIssuableApiKeyScopes();

    expect(isOk(result)).toBe(true);
    if (isOk(result)) {
      expect(result.value).toEqual(scopeCatalog);
    }
    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5000/api/apikey/scopes",
      expect.objectContaining({ method: "GET" })
    );
  });

  it("keeps the legacy alias working for existing callers", async () => {
    const fetchMock = installFetchMock();
    const client = new AzoaApiClient({ baseUrl: "http://localhost:5000" });

    const result = await client.listIssuableScopes();

    expect(isOk(result)).toBe(true);
    if (isOk(result)) {
      expect(result.value).toEqual(scopeCatalog);
    }
    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5000/api/apikey/scopes",
      expect.objectContaining({ method: "GET" })
    );
  });
});
