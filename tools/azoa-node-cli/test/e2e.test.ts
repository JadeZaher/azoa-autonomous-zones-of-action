import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { runAdminsActivate, runAdminsCreate } from "../src/commands/admins.js";
import { runApi } from "../src/commands/api.js";
import { runEnroll } from "../src/commands/enroll.js";
import { runKeygen } from "../src/commands/keygen.js";
import { runLogin } from "../src/commands/login.js";
import { AzoaCliError } from "../src/errors.js";
import { readKeyFileMeta } from "../src/storage/keyFile.js";
import { upsertProfile } from "../src/storage/config.js";
import { FakeInputSource } from "./fixtures/fakeInput.js";
import { FakeNodeServer } from "./fixtures/fakeNodeServer.js";
import { totp } from "./fixtures/totp.js";

describe("end-to-end CLI <-> fake node (plan.md §3/§8 contract)", () => {
  let home: string;
  let server: FakeNodeServer;
  let origin: string;

  beforeEach(async () => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-e2e-"));
    process.env.AZOA_HOME = home;
    server = new FakeNodeServer({ audience: "" });
    origin = await server.listen();
    server.audience = origin;
  });

  afterEach(async () => {
    await server.close();
    delete process.env.AZOA_HOME;
    rmSync(home, { recursive: true, force: true });
  });

  it("root login: keygen -> profile -> challenge/sign/session round trip", async () => {
    const input = new FakeInputSource({ secrets: ["rootpass1", "rootpass1", "rootpass1"] });
    await runKeygen({ name: "root1", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root1");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp", { node: origin, keyName: "root1", kind: "root" }, true);

    const session = await runLogin({ profile: "rootp", root: true, json: false }, input);

    expect(session.principal).toBe(`root:${meta.fingerprint}`);
    expect(session.permissions).toContain("admins.manage");
    expect(server.sessions.has(session.sid)).toBe(true);
  });

  it("aborts before signing when the challenge audience does not match the pinned profile node (H1)", async () => {
    server.audience = "https://not-the-real-node.example.test"; // server is reachable at `origin`, but claims a different audience
    const input = new FakeInputSource({ secrets: ["rootpass1", "rootpass1"] });
    await runKeygen({ name: "root2", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root2");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp2", { node: origin, keyName: "root2", kind: "root" }, true);

    await expect(runLogin({ profile: "rootp2", root: true, json: false }, input)).rejects.toThrow(/Audience mismatch/);

    // The CLI must never have reached /auth/session with this mismatched audience.
    expect(server.requestLog.some((r) => r.path === "/api/node-admin/auth/session")).toBe(false);
    expect(server.requestLog.some((r) => r.path === "/api/node-admin/auth/challenge")).toBe(true);
  });

  it("full admin lifecycle: create -> enroll (stdin token) -> activate -> login with TOTP", async () => {
    // Root bootstraps and creates the admin (2 secrets for keygen's passphrase+confirm, 1 for login).
    const rootInput = new FakeInputSource({ secrets: ["rootpass1", "rootpass1", "rootpass1"] });
    await runKeygen({ name: "root3", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("root3");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootp3", { node: origin, keyName: "root3", kind: "root" }, true);
    await runLogin({ profile: "rootp3", root: true, json: false }, rootInput);

    const created = await runAdminsCreate(
      { username: "alice", displayName: "Alice", roleIds: ["role-node-owner"] },
      { profile: "rootp3", json: false },
    );
    expect(created.admin.status).toBe("pending_enrollment");

    // Alice enrolls from her own machine: token via stdin (never argv), TOTP computed from the
    // secret the server just generated (looked up live — it does not exist before /enroll/start).
    const enrollInput = new FakeInputSource({
      piped: created.enrollmentToken,
      visibles: [() => {
        const [enrollment] = [...server.enrollments.values()];
        return totp(enrollment!.totpSecretPending, Math.floor(Date.now() / 1000));
      }],
    });
    await runEnroll(
      { node: origin, profile: "alice", keyName: "alice-key", label: "laptop", passphrase: false, force: false, json: false },
      enrollInput,
    );

    const aliceMeta = readKeyFileMeta("alice-key");
    const refreshedAdmin = server.admins.get(created.admin.id)!;
    expect(refreshedAdmin.status).toBe("pending_activation");
    expect(refreshedAdmin.keys[0]!.fingerprint).toBe(aliceMeta.fingerprint);

    // A dominating admin (root here) confirms the fingerprint out of band and activates.
    await runAdminsActivate(created.admin.id, aliceMeta.fingerprint, { profile: "rootp3", json: false });
    expect(server.admins.get(created.admin.id)!.status).toBe("active");

    // Alice can now log in with her key + TOTP.
    const loginInput = new FakeInputSource({
      visibles: [() => totp(server.admins.get(created.admin.id)!.totpSecret!, Math.floor(Date.now() / 1000))],
    });
    const aliceSession = await runLogin({ profile: "alice", json: false }, loginInput);
    expect(aliceSession.principal).toBe(`admin:${created.admin.id}`);
    expect(aliceSession.permissions).toContain("admins.manage");
  });

  it("STEP_UP_REQUIRED on `api` triggers a transparent re-login and a single retry", async () => {
    server.recentWindowMs = 1000; // force staleness quickly without a real 10-minute wait
    // 2 for keygen (passphrase + confirm), then one passphrase prompt per login: the explicit
    // login below, and the transparent re-login `runApi` triggers on STEP_UP_REQUIRED.
    const input = new FakeInputSource({ secrets: ["rootpass1", "rootpass1", "rootpass1", "rootpass1"] });
    await runKeygen({ name: "root4", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root4");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp4", { node: origin, keyName: "root4", kind: "root" }, true);
    await runLogin({ profile: "rootp4", root: true, json: false }, input);

    await new Promise((resolve) => setTimeout(resolve, 1200)); // session is now stale relative to recentWindowMs

    const result = await runApi("GET", "/api/example/protected", { profile: "rootp4", json: false }, input);
    expect(result).toEqual({ ok: true, principal: `root:${meta.fingerprint}` });

    const protectedHits = server.requestLog.filter((r) => r.path === "/api/example/protected");
    expect(protectedHits.length).toBe(2); // first 403, then the retry after re-login
  });

  it("`api` rejects an absolute URL before any network call", async () => {
    upsertProfile("rootp5", { node: origin, keyName: "root5", kind: "root" }, true);
    // No session needed — the path guard runs before auth is checked, for both absolute-URL shapes.
    await expect(runApi("GET", "https://evil.example.test/api/node-admin/me", { profile: "rootp5", json: false })).rejects.toThrow(AzoaCliError);
    await expect(runApi("GET", "https://evil.example.test/api/node-admin/me", { profile: "rootp5", json: false })).rejects.toThrow(/must start with '\/api\/'/);
    // Protocol-relative ("//host/...") does start with neither a scheme nor literally "/api/" either, so it is caught the same way.
    await expect(runApi("GET", "//evil.example.test/api/node-admin/me", { profile: "rootp5", json: false })).rejects.toThrow(/must start with '\/api\/'/);
    expect(server.requestLog.length).toBe(0);
  });
});
