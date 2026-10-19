import { mkdtempSync, rmSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, it, vi } from "vitest";
import { runLogout } from "../src/commands/logout.js";
import { upsertProfile } from "../src/storage/config.js";
import { loadSession, saveSession } from "../src/storage/session.js";

it("preserves the local token and reports failure when revocation cannot be confirmed", async () => {
  const home = mkdtempSync(join(tmpdir(), "azoa-cli-logout-"));
  const originalHome = process.env.AZOA_HOME;
  process.env.AZOA_HOME = home;
  const requests: string[] = [];
  const server = createServer((request, response) => {
    requests.push(`${request.method} ${request.url}`);
    response.writeHead(503, { "content-type": "application/json" });
    response.end(JSON.stringify({ isError: true, code: "AUDIT_UNAVAILABLE", message: "Audit unavailable." }));
  });
  const output = vi.spyOn(console, "log").mockImplementation(() => undefined);
  try {
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    if (!address || typeof address === "string") throw new Error("failed to bind");
    upsertProfile("stale", { node: `http://127.0.0.1:${address.port}`, keyName: "unused", kind: "root" }, true);
    saveSession("stale", {
      accessToken: "original-token", handle: "original-handle", principal: "root:21fe31dfa154a261",
      authTime: new Date(Date.now() - 11 * 60_000).toISOString(), expiresAt: new Date(Date.now() + 60_000).toISOString(), permissions: [],
    });

    await expect(runLogout({ profile: "stale", json: true })).rejects.toMatchObject({ code: "AUDIT_UNAVAILABLE", exitCode: 5 });
    expect(loadSession("stale")?.accessToken).toBe("original-token");
    expect(requests).toEqual(["POST /api/node-admin/auth/revoke"]);
    expect(output).not.toHaveBeenCalled();
  } finally {
    output.mockRestore();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    if (originalHome === undefined) delete process.env.AZOA_HOME;
    else process.env.AZOA_HOME = originalHome;
    rmSync(home, { recursive: true, force: true });
  }
});
