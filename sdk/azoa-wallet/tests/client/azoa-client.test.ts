import { describe, it, expect } from "vitest";
import { AzoaClient } from "../../src/client/azoa-client.js";

describe("AzoaClient.getApiUrl (Phase 2 prereq Finding 4b)", () => {
  it("returns the configured API base URL", () => {
    const azoa = new AzoaClient({ apiUrl: "https://api.example.test" });
    expect(azoa.getApiUrl()).toBe("https://api.example.test");
  });

  it("returns the configured API base URL after switching network", () => {
    const azoa = new AzoaClient({
      apiUrl: "https://api.devnet.example",
      network: "devnet",
    });
    expect(azoa.getApiUrl()).toBe("https://api.devnet.example");
    azoa.setNetwork("testnet");
    // API URL is independent of network switching
    expect(azoa.getApiUrl()).toBe("https://api.devnet.example");
  });
});
