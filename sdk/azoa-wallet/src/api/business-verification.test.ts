import { afterEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "./client.js";

afterEach(() => vi.unstubAllGlobals());

describe("business verification", () => {
  it("sends tenant-bound paths and the caller's stable admission key", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ isError: false, result: {
      verification: { attemptId: "attempt", status: "PENDING", verified: false, expiresAt: "2026-10-18T00:00:00Z", beneficiaryCount: 0 },
      verificationUrl: "https://widget.identomat.com/?session_token=fixture", resumed: false,
    } }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetch);
    const client = new AzoaApiClient({ baseUrl: "https://node.example", apiKey: "tenant-key" });
    const result = await client.beginBusinessVerification("stable-attempt");
    expect(result.ok).toBe(true);
    expect(fetch).toHaveBeenCalledOnce();
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe("https://node.example/api/tenant/business-verification/session");
    expect(new Headers(init.headers).get("Idempotency-Key")).toBe("stable-attempt");
    expect(init.body).toBeUndefined();
  });

  it("refreshes through the authenticated backend without a provider session token", async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ isError: false, result: null }), { status: 200 }));
    vi.stubGlobal("fetch", fetch);
    await new AzoaApiClient({ baseUrl: "https://node.example", token: "login" }).refreshBusinessVerification();
    expect(fetch.mock.calls[0][0]).toBe("https://node.example/api/tenant/business-verification/refresh");
    expect(fetch.mock.calls[0][1].method).toBe("POST");
  });
});
