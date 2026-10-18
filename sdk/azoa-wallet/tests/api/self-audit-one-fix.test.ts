/**
 * Tests for self-audit-one-fix findings.
 * Covers: listNfts (F6), getComposite path (F8),
 * getApiUrl (F4), updateActionGraphBundle PUT (F7); F1/F2 client DEX adapters and legacy swap routes are retired.
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { AzoaClient } from "../../src/client/azoa-client.js";
import { HolonQueryBuilder } from "../../src/client/holon-query.js";
import { API_PATHS } from "../../src/api/api-version.js";
import { isOk } from "../../src/core/result.js";
import { ApiConfigBuilder } from "../builders/index.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message: "Success", result }),
  });
}

describe("Finding 6 — listNfts() on AzoaApiClient", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
  });

  it("sends GET /api/nft with no params", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse([]));
    const result = await client.listNfts();
    expect(isOk(result)).toBe(true);
    expect(mockFetch).toHaveBeenCalledWith(
      "http://localhost:5000/api/nft",
      expect.objectContaining({ method: "GET" })
    );
  });

  it("sends GET /api/nft?ownerUserId=... when param provided", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse([{ id: "nft1", name: "My NFT" }]));
    const result = await client.listNfts({ ownerUserId: "user-abc" });
    expect(isOk(result)).toBe(true);
    const url: string = mockFetch.mock.calls[0][0];
    expect(url).toContain("/api/nft");
    expect(url).toContain("ownerUserId=user-abc");
  });

  it("listNfts returns typed NftResult array", async () => {
    const nfts = [
      { id: "n1", name: "NFT One", description: "d", chainId: "algorand", isActive: true },
    ];
    mockFetch.mockReturnValueOnce(azoaResponse(nfts));
    const result = await client.listNfts({ chainId: "algorand" });
    expect(isOk(result)).toBe(true);
    if (result.ok) {
      expect(result.value).toHaveLength(1);
      expect(result.value[0]!.name).toBe("NFT One");
    }
  });
});

describe("Finding 8 — getComposite() uses API_PATHS.HOLON_COMPOSE", () => {
  let client: AzoaApiClient;
  let holons: HolonQueryBuilder;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
    holons = new HolonQueryBuilder(client);
  });

  it("API_PATHS.HOLON_COMPOSE produces correct path", () => {
    expect(API_PATHS.HOLON_COMPOSE("some-holon-id")).toBe("/api/holon/some-holon-id/compose");
  });

  it("getComposite() sends GET /api/holon/{id}/compose", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: "h1", children: [] }));
    const result = await holons.getComposite("holon-123");
    expect(isOk(result)).toBe(true);
    expect(mockFetch).toHaveBeenCalledWith(
      "http://localhost:5000/api/holon/holon-123/compose",
      expect.objectContaining({ method: "GET" })
    );
  });
});

describe("Finding 4 — getApiUrl() on AzoaClient and AzoaApiClient", () => {
  it("AzoaApiClient.getBaseUrl() returns the configured baseUrl", () => {
    const client = new AzoaApiClient({ baseUrl: "https://api.example.com" });
    expect(client.getBaseUrl()).toBe("https://api.example.com");
  });

  it("AzoaClient.getApiUrl() returns the apiUrl from config", () => {
    const azoa = new AzoaClient({ apiUrl: "https://azoa.staging.io" });
    expect(azoa.getApiUrl()).toBe("https://azoa.staging.io");
  });
});

// Finding 2 (legacy /api/swap quote/execute) is retired: swaps run through the durable /api/dex lifecycle (tests/api/client.dex.test.ts).

describe("Finding 7 — updateActionGraphBundle() uses PUT /api/actiongraphbundle/{id}", () => {
  let client: AzoaApiClient;
  const VALID_ID = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
  });

  it("issues PUT to /api/actiongraphbundle/{id}", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: VALID_ID, name: "MyApp" }));
    const result = await client.updateActionGraphBundle(VALID_ID, { name: "MyApp", description: "desc" });
    expect(isOk(result)).toBe(true);
    const [url, init] = mockFetch.mock.calls[0];
    expect(init.method).toBe("PUT");
    expect(url).toBe(`http://localhost:5000/api/actiongraphbundle/${VALID_ID}`);
  });

  it("embeds the id in the URL path, not the body", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: VALID_ID, name: "MyApp" }));
    await client.updateActionGraphBundle(VALID_ID, { name: "MyApp", description: "desc" });
    const [url] = mockFetch.mock.calls[0];
    expect(url).toContain(`/api/actiongraphbundle/${VALID_ID}`);
  });

  it("throws before sending a request when id is not a UUID", async () => {
    await expect(client.updateActionGraphBundle("not-a-uuid", { name: "MyApp", description: "desc" })).rejects.toThrow(
      "Invalid actiongraphbundleId"
    );
    expect(mockFetch).not.toHaveBeenCalled();
  });
});
