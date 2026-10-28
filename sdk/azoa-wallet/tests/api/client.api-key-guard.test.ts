import { afterEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { AzoaApiConfig } from "../../src/api/client.js";

const baseUrl = "https://node.azoa.test";

/** Every header the node received, per request. */
function recordingNode() {
  const headers: Record<string, string>[] = [];
  vi.stubGlobal("fetch", async (_input: RequestInfo | URL, init?: RequestInit) => {
    headers.push({ ...(init?.headers as Record<string, string>) });
    return new Response(JSON.stringify({ isError: false, message: "Success", result: { available: true } }), { status: 200 });
  });
  return headers;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("AzoaApiClient refuses a tenant API key in a browser (M3)", () => {
  it.each([
    ["a page", { window: { document: {} }, document: {} }],
    ["a dedicated worker", { WorkerGlobalScope: function WorkerGlobalScope() {}, importScripts: () => undefined }],
  ])("throws in %s", (_label, browserGlobals) => {
    for (const [name, value] of Object.entries(browserGlobals)) vi.stubGlobal(name, value);

    expect(() => new AzoaApiClient({ baseUrl, apiKey: "azoa_live_tenant_key" })).toThrow(/apiKey in a browser/);
    // A non-string but truthy key is still sent as X-Api-Key, so it is refused the same way.
    expect(() => new AzoaApiClient({ baseUrl, apiKey: 12345 as unknown as string })).toThrow(/apiKey in a browser/);
  });

  it("a browser session token without a key still works there", () => {
    vi.stubGlobal("document", {});
    expect(() => new AzoaApiClient({ baseUrl, token: "login-jwt" })).not.toThrow();
  });

  it("the loudly named opt-out admits the key in a trusted browser runtime", () => {
    vi.stubGlobal("document", {});
    expect(new AzoaApiClient({ baseUrl, apiKey: "k", dangerouslyAllowApiKeyInBrowser: true }).hasApiKey()).toBe(true);
  });

  it("server-side (Node) use with an apiKey keeps working and sends it", async () => {
    const headers = recordingNode();
    const client = new AzoaApiClient({ baseUrl, apiKey: "azoa_live_tenant_key" });

    await client.getFiatCheckoutCapabilities();

    expect(client.hasApiKey()).toBe(true);
    expect(headers[0]?.["X-Api-Key"]).toBe("azoa_live_tenant_key");
  });

  it("copies its config: a key added to the caller's object afterwards is never sent and hasApiKey agrees", async () => {
    const headers = recordingNode();
    const config: AzoaApiConfig = { baseUrl };
    const client = new AzoaApiClient(config);

    config.apiKey = "added-after-the-guard";
    await client.getFiatCheckoutCapabilities();

    expect(client.hasApiKey()).toBe(false);
    expect(headers[0]?.["X-Api-Key"]).toBeUndefined();
  });

  it("hasApiKey uses the same truthiness the request uses", async () => {
    const headers = recordingNode();
    const client = new AzoaApiClient({ baseUrl, apiKey: 12345 as unknown as string });

    await client.getFiatCheckoutCapabilities();

    expect(headers[0]?.["X-Api-Key"]).toBe(12345);
    expect(client.hasApiKey()).toBe(true);
  });
});
