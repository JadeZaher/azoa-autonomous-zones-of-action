import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { runAdminsActivate, runAdminsCreate } from "../src/commands/admins.js";
import { runApi } from "../src/commands/api.js";
import { runAuditVerify } from "../src/commands/audit.js";
import { runEnroll } from "../src/commands/enroll.js";
import { runKeysAdd } from "../src/commands/keysCmd.js";
import { runKeygen } from "../src/commands/keygen.js";
import { runLogin } from "../src/commands/login.js";
import { runRootRevokeSessions } from "../src/commands/rootRevokeSessions.js";
import { AzoaCliError } from "../src/errors.js";
import { readKeyFileMeta } from "../src/storage/keyFile.js";
import { keyPath } from "../src/storage/paths.js";
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
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "root1", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root1");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp", { node: origin, keyName: "root1", kind: "root" }, true);

    const session = await runLogin({ profile: "rootp", root: true, json: false }, input);

    expect(session.principal).toBe(`root:${meta.fingerprint}`);
    expect(session.permissions).toContain("admins.manage");
    // `server.sessions` is keyed by the real sid (the bearer/accessToken); `handle` is the
    // separate, wire-exposed id (lane A1 M-1) and is NOT a key into that map.
    expect(server.sessions.has(session.accessToken)).toBe(true);

    // Lane A1 deviation #16: challengeId is opaque (~300-char base64url), not a GUID, and the
    // login above only succeeded by signing it verbatim — proving the CLI never parses/validates
    // its shape or length-limits it.
    const [issuedChallengeId] = [...server.challenges.keys()];
    expect(issuedChallengeId).toBeDefined();
    expect(issuedChallengeId!.length).toBeGreaterThan(100);
    expect(issuedChallengeId).not.toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  });

  it("aborts before signing when the challenge audience does not match the pinned profile node (H1)", async () => {
    server.audience = "https://not-the-real-node.example.test"; // server is reachable at `origin`, but claims a different audience
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "root2", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root2");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp2", { node: origin, keyName: "root2", kind: "root" }, true);

    await expect(runLogin({ profile: "rootp2", root: true, json: false }, input)).rejects.toThrow(/Audience mismatch/);

    // The CLI must never have reached /auth/session with this mismatched audience.
    expect(server.requestLog.some((r) => r.path === "/api/node-admin/auth/session")).toBe(false);
    expect(server.requestLog.some((r) => r.path === "/api/node-admin/auth/challenge")).toBe(true);
  });

  it("enroll: aborts before /enroll/complete when /enroll/start's audience disagrees with --origin (H1-style)", async () => {
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootE1", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("rootE1");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootpE1", { node: origin, keyName: "rootE1", kind: "root" }, true);
    await runLogin({ profile: "rootpE1", root: true, json: false }, rootInput);
    const created = await runAdminsCreate(
      { username: "bob", displayName: "Bob", roleIds: ["node-owner"] },
      { profile: "rootpE1", json: false },
    );

    server.audience = "https://not-the-real-node.example.test"; // reachable at `origin`, but claims a different audience

    const enrollInput = new FakeInputSource({ piped: created.enrollmentToken, visibles: ["000000"] }); // never reached
    await expect(
      runEnroll(
        { node: origin, profile: "bob", keyName: "bob-key", label: "laptop", passphrase: false, force: false, json: false },
        enrollInput,
      ),
    ).rejects.toThrow(/Audience mismatch/);

    expect(server.requestLog.some((r) => r.path === "/api/node-admin/enroll/complete")).toBe(false);
    expect(server.requestLog.some((r) => r.path === "/api/node-admin/enroll/start")).toBe(true);
  });

  it("enroll: aborts before /enroll/complete when /enroll/start's returned fingerprint disagrees with the local key", async () => {
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootE2", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("rootE2");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootpE2", { node: origin, keyName: "rootE2", kind: "root" }, true);
    await runLogin({ profile: "rootpE2", root: true, json: false }, rootInput);
    const created = await runAdminsCreate(
      { username: "carol", displayName: "Carol", roleIds: ["node-owner"] },
      { profile: "rootpE2", json: false },
    );

    server.forceEnrollFingerprint = "ffffffffffffffff"; // simulate a tampered/misbehaving response

    const enrollInput = new FakeInputSource({ piped: created.enrollmentToken, visibles: ["000000"] }); // never reached
    await expect(
      runEnroll(
        { node: origin, profile: "carol", keyName: "carol-key", label: "laptop", passphrase: false, force: false, json: false },
        enrollInput,
      ),
    ).rejects.toThrow(/Fingerprint mismatch/);

    expect(server.requestLog.some((r) => r.path === "/api/node-admin/enroll/complete")).toBe(false);
  });

  it("ADMIN_LOCKED after 5 consecutive bad TOTP attempts: CLI surfaces the retry time, exit class AUTH", async () => {
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootL1", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("rootL1");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootpL1", { node: origin, keyName: "rootL1", kind: "root" }, true);
    await runLogin({ profile: "rootpL1", root: true, json: false }, rootInput);
    const created = await runAdminsCreate(
      { username: "dave", displayName: "Dave", roleIds: ["node-owner"] },
      { profile: "rootpL1", json: false },
    );

    const enrollInput = new FakeInputSource({
      piped: created.enrollmentToken,
      visibles: [() => {
        const [enrollment] = [...server.enrollments.values()];
        return totp(enrollment!.totpSecretPending, Math.floor(Date.now() / 1000));
      }],
    });
    await runEnroll(
      { node: origin, profile: "dave", keyName: "dave-key", label: "laptop", passphrase: false, force: false, json: false },
      enrollInput,
    );
    const daveMeta = readKeyFileMeta("dave-key");
    await runAdminsActivate(created.admin.id, daveMeta.fingerprint, { profile: "rootpL1", json: false });

    // 5 consecutive second-factor failures lock the admin (AGENTS.md §auth / SECOND_FACTOR_LOCK_THRESHOLD).
    for (let attempt = 0; attempt < 5; attempt++) {
      await expect(runLogin({ profile: "dave", totp: "000000", json: false })).rejects.toThrow(AzoaCliError);
    }

    let caught: AzoaCliError | undefined;
    try {
      await runLogin({ profile: "dave", totp: "000000", json: false });
    } catch (err) {
      caught = err as AzoaCliError;
    }
    expect(caught).toBeInstanceOf(AzoaCliError);
    expect(caught!.code).toBe("ADMIN_LOCKED");
    expect(caught!.exitCode).toBe(3);
    expect(caught!.message).toMatch(/15m/); // formatted Retry-After (default 15 min lock)
  });

  it("full admin lifecycle: create -> enroll (stdin token) -> activate -> login with TOTP", async () => {
    // Root bootstraps and creates the admin (2 secrets for keygen's passphrase+confirm, 1 for login).
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "root3", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("root3");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootp3", { node: origin, keyName: "root3", kind: "root" }, true);
    await runLogin({ profile: "rootp3", root: true, json: false }, rootInput);

    const created = await runAdminsCreate(
      { username: "alice", displayName: "Alice", roleIds: ["node-owner"] }, // built-in role id == name (lane A1 deviation #8)
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
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456", "rootpass123456"] });
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

  it("root revoke-sessions: fails fast for a non-root profile, zero network calls", async () => {
    upsertProfile("notroot", { node: origin, keyName: "whatever", kind: "admin" }, true);

    await expect(runRootRevokeSessions({ profile: "notroot", yes: true, json: false })).rejects.toThrow(AzoaCliError);
    let caught: AzoaCliError | undefined;
    try {
      await runRootRevokeSessions({ profile: "notroot", yes: true, json: false });
    } catch (err) {
      caught = err as AzoaCliError;
    }
    expect(caught!.code).toBe("PERMISSION_DENIED");
    expect(caught!.exitCode).toBe(4);
    expect(caught!.message).toMatch(/RootKeyRevision/);
    expect(server.requestLog.length).toBe(0);
  });

  it("root revoke-sessions: succeeds for root, exercising the generic step-up retry on a non-keys/api command", async () => {
    server.recentWindowMs = 1000;
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "root6", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root6");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp6", { node: origin, keyName: "root6", kind: "root" }, true);
    await runLogin({ profile: "rootp6", root: true, json: false }, input);

    await new Promise((resolve) => setTimeout(resolve, 1200)); // session now stale

    await runRootRevokeSessions({ profile: "rootp6", yes: true, json: false }, input);

    const hits = server.requestLog.filter((r) => r.path === "/api/node-admin/root/revoke-sessions");
    expect(hits.length).toBe(2); // first 403 STEP_UP_REQUIRED, then the retry after re-login succeeds
  });

  it("audit verify --full surfaces RATE_LIMITED (429) as exit 5 with a retry hint", async () => {
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "root7", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("root7");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootp7", { node: origin, keyName: "root7", kind: "root" }, true);
    await runLogin({ profile: "rootp7", root: true, json: false }, input);

    server.forceAuditRateLimited = true;

    let caught: AzoaCliError | undefined;
    try {
      await runAuditVerify({ profile: "rootp7", full: true, json: false });
    } catch (err) {
      caught = err as AzoaCliError;
    }
    expect(caught!.code).toBe("RATE_LIMITED");
    expect(caught!.exitCode).toBe(5);
    expect(caught!.message).toMatch(/minute/i);
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

  it("`api` rejects a dot-segment path that resolves outside /api/ even though the raw string starts with /api/", async () => {
    upsertProfile("rootp5b", { node: origin, keyName: "root5b", kind: "root" }, true);
    await expect(runApi("GET", "/api/../secret", { profile: "rootp5b", json: false })).rejects.toThrow(/resolves to/);
    expect(server.requestLog.length).toBe(0);
  });

  it("enroll (M4): deletes the just-written key file when /enroll/complete fails", async () => {
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootM4a", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("rootM4a");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootpM4a", { node: origin, keyName: "rootM4a", kind: "root" }, true);
    await runLogin({ profile: "rootpM4a", root: true, json: false }, rootInput);
    const created = await runAdminsCreate(
      { username: "erin", displayName: "Erin", roleIds: ["node-owner"] },
      { profile: "rootpM4a", json: false },
    );

    // Deliberately wrong TOTP so /enroll/complete fails with ENROLLMENT_INVALID.
    const enrollInput = new FakeInputSource({ piped: created.enrollmentToken, visibles: ["000000"] });
    await expect(
      runEnroll(
        { node: origin, profile: "erin", keyName: "erin-key", label: "laptop", passphrase: false, force: false, json: false },
        enrollInput,
      ),
    ).rejects.toThrow(AzoaCliError);

    expect(existsSync(keyPath("erin-key"))).toBe(false); // the key the server never registered is not left on disk
  });

  it("keys add (M4): deletes the just-written key file when the server call fails", async () => {
    const rootInput = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootM4b", kind: "root", passphrase: true, json: false }, rootInput);
    const rootMeta = readKeyFileMeta("rootM4b");
    server.rootPublicKeys.add(rootMeta.publicKey);
    upsertProfile("rootpM4b", { node: origin, keyName: "rootM4b", kind: "root" }, true);
    await runLogin({ profile: "rootpM4b", root: true, json: false }, rootInput);

    // "nonexistent-admin-id" never exists on the fake server -> NOT_FOUND, deterministically, with
    // no need to exercise signature/step-up machinery first.
    await expect(
      runKeysAdd(
        { profile: "rootpM4b", targetAdminId: "nonexistent-admin-id", keyName: "orphan-key", label: "cli", passphrase: false, force: false, json: false },
        rootInput,
      ),
    ).rejects.toThrow(AzoaCliError);

    expect(existsSync(keyPath("orphan-key"))).toBe(false);
  });

  it("audit verify (pin flow, e2e over real HTTP against the fake server): a tampered row is caught, and a broken chain is never pinned", async () => {
    const input = new FakeInputSource({ secrets: ["rootpass123456", "rootpass123456", "rootpass123456"] });
    await runKeygen({ name: "rootAudit", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("rootAudit");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("rootpAudit", { node: origin, keyName: "rootAudit", kind: "root" }, true);
    await runLogin({ profile: "rootpAudit", root: true, json: false }, input); // produces the audit row we're about to tamper

    // Tamper with a row's detail, without touching its stored hash — exactly the shape the
    // local recompute (fetched fresh over HTTP, not a pre-built fixture) is meant to catch, and
    // BEFORE any pin exists — once a row falls behind an established pin, neither side re-walks
    // it on an incremental verify (the documented H5 "detectable, not impossible" scope).
    const row = server.auditRows[0]!;
    const originalDetail = row.detail;
    row.detail = '{"tampered":true}';

    await expect(runAuditVerify({ profile: "rootpAudit", json: false })).rejects.toThrow(/AUDIT INTEGRITY ALERT/);

    // The broken chain must never get pinned — restore the original detail (the stored hash was
    // always computed over IT, not over the tamper) and confirm verify now succeeds, having
    // walked from genesis since no pin was written by the failed attempt above.
    row.detail = originalDetail;
    await runAuditVerify({ profile: "rootpAudit", json: false });
    const { loadAuditPin } = await import("../src/storage/auditPin.js");
    expect(loadAuditPin("rootpAudit")).toEqual({ seq: row.seq, hash: row.hash });
  });
});
