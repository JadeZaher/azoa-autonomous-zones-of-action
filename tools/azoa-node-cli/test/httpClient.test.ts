import { createServer, type Server } from "node:http";
import { describe, expect, it } from "vitest";
import { AzoaCliError } from "../src/errors.js";
import { AzoaHttpClient } from "../src/http/client.js";
import { FakeNodeServer } from "./fixtures/fakeNodeServer.js";

/** A bare server that answers with a given status and NO body — exactly what ASP.NET Core's JWT
 * bearer middleware emits for a rejected token, before any controller or AZOAResult envelope
 * exists (join-report.md fix target). */
async function listenWithBareStatus(status: number): Promise<{ origin: string; server: Server }> {
  const server = createServer((_req, res) => {
    res.statusCode = status;
    res.end();
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("failed to bind");
  return { origin: `http://127.0.0.1:${address.port}`, server };
}

/**
 * H1 (coordinator review of 561adf31): a `--node` global override let an authenticated command
 * be redirected to a different origin while still attaching the real bearer token. The override
 * is gone entirely (src/context.ts, src/cli.ts), and this is the defense-in-depth backstop the
 * reviewer asked for directly in the HTTP client: a bearer is never attached to a request whose
 * resolved URL origin disagrees with the client's own configured origin — proved here with a
 * SECOND real server on a second port that must receive zero requests, let alone an
 * Authorization header.
 */
describe("AzoaHttpClient never sends its bearer cross-origin", () => {
  it("resolveUrl rejects an absolute URL pointing at a different origin before any fetch, so the other server sees nothing", async () => {
    const serverA = new FakeNodeServer({ audience: "" });
    const originA = await serverA.listen();
    serverA.audience = originA;

    const serverB = new FakeNodeServer({ audience: "" });
    const originB = await serverB.listen();
    serverB.audience = originB;

    try {
      const client = new AzoaHttpClient(originA, () => "super-secret-token");

      await expect(client.raw("GET", `${originB}/api/node-admin/me`)).rejects.toThrow(/cross-origin/);

      expect(serverB.requestLog.length).toBe(0); // zero requests reached B at all
      expect(serverA.requestLog.length).toBe(0); // and none were sent to A either — it never got that far
    } finally {
      await serverA.close();
      await serverB.close();
    }
  });

  it("attaches the bearer for same-origin requests (sanity check the guard isn't just always-throw)", async () => {
    const server = new FakeNodeServer({ audience: "" });
    const origin = await server.listen();
    server.audience = origin;
    try {
      const client = new AzoaHttpClient(origin, () => "some-token");
      await client.raw("GET", "/api/node-admin/me"); // 401 (no real session) is fine — we only care that it reached the server
      expect(server.requestLog.some((r) => r.path === "/api/node-admin/me")).toBe(true);
    } finally {
      await server.close();
    }
  });
});

describe("AzoaHttpClient maps un-enveloped (bare, empty-body) 4xx to the matching error-code family (join-report.md fix)", () => {
  it.each([
    [401, "AUTH_FAILED", 3],
    [403, "PERMISSION_DENIED", 4],
    [404, "NOT_FOUND", 2],
    [409, "CONFLICT", 2],
    [423, "ADMIN_LOCKED", 3],
    [429, "RATE_LIMITED", 5],
  ] as const)("bare %i -> code %s -> exit %i (not NETWORK/5 from a missing envelope)", async (status, code, exitCode) => {
    const { origin, server } = await listenWithBareStatus(status);
    try {
      const client = new AzoaHttpClient(origin);
      let caught: AzoaCliError | undefined;
      try {
        await client.call("GET", "/api/node-admin/me");
      } catch (err) {
        caught = err as AzoaCliError;
      }
      expect(caught).toBeInstanceOf(AzoaCliError);
      expect(caught!.code).toBe(code);
      expect(caught!.exitCode).toBe(exitCode);
    } finally {
      server.close();
    }
  });

  it("still falls back to NETWORK(5) for a bare status with no mapped code", async () => {
    const { origin, server } = await listenWithBareStatus(500);
    try {
      const client = new AzoaHttpClient(origin);
      let caught: AzoaCliError | undefined;
      try {
        await client.call("GET", "/api/node-admin/me");
      } catch (err) {
        caught = err as AzoaCliError;
      }
      expect(caught!.code).toBeUndefined();
      expect(caught!.exitCode).toBe(5);
    } finally {
      server.close();
    }
  });
});
