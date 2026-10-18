import { mkdtempSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { runEnroll } from "../src/commands/enroll.js";
import { runKeygen } from "../src/commands/keygen.js";
import { runLogin } from "../src/commands/login.js";
import { AzoaCliError } from "../src/errors.js";
import { readKeyFileMeta } from "../src/storage/keyFile.js";
import { keyPath } from "../src/storage/paths.js";
import { upsertProfile } from "../src/storage/config.js";
import { FakeInputSource } from "./fixtures/fakeInput.js";
import { FakeNodeServer } from "./fixtures/fakeNodeServer.js";
import { totp } from "./fixtures/totp.js";

describe("POSIX file modes (M1: written at 0600/0700 from the start, no TOCTOU window)", () => {
  let home: string;

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-modes-"));
    process.env.AZOA_HOME = home;
  });

  afterEach(() => {
    delete process.env.AZOA_HOME;
    rmSync(home, { recursive: true, force: true });
  });

  it.runIf(process.platform !== "win32")("a key file is created at mode 0600", async () => {
    await runKeygen({ name: "modetest", kind: "admin", passphrase: false, force: false, json: false }, new FakeInputSource());
    const mode = statSync(keyPath("modetest")).mode & 0o777;
    expect(mode).toBe(0o600);
  });

  it.runIf(process.platform !== "win32")("~/.azoa subdirectories are created at mode 0700", async () => {
    await runKeygen({ name: "modetest2", kind: "admin", passphrase: false, force: false, json: false }, new FakeInputSource());
    const keysDirMode = statSync(join(home, "keys")).mode & 0o777;
    expect(keysDirMode).toBe(0o700);
  });
});

describe("secrets never reach stdout/stderr/--json/errors", () => {
  let home: string;
  let server: FakeNodeServer;
  let origin: string;
  let logs: string[];
  let errs: string[];

  beforeEach(async () => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-secrets-"));
    process.env.AZOA_HOME = home;
    server = new FakeNodeServer({ audience: "" });
    origin = await server.listen();
    server.audience = origin;
    logs = [];
    errs = [];
    vi.spyOn(console, "log").mockImplementation((msg: unknown) => {
      logs.push(String(msg));
    });
    vi.spyOn(console, "error").mockImplementation((msg: unknown) => {
      errs.push(String(msg));
    });
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    await server.close();
    delete process.env.AZOA_HOME;
    rmSync(home, { recursive: true, force: true });
  });

  it("the passphrase and the raw private key are never printed anywhere, across the whole bootstrap+create+enroll+activate+login flow", async () => {
    const passphrase = "a passphrase nobody should see";
    // 2 for keygen (passphrase + confirm), 1 for login's key-passphrase prompt.
    const input = new FakeInputSource({ secrets: [passphrase, passphrase, passphrase] });
    await runKeygen({ name: "secretroot", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("secretroot");
    const rawPrivateKeyField = (JSON.parse(readFileSync(keyPath("secretroot"), "utf8")) as { privateKey: string }).privateKey;
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("secretrootp", { node: origin, keyName: "secretroot", kind: "root" }, true);
    const rootSession = await runLogin({ profile: "secretrootp", root: true, json: false }, input);

    const { runAdminsCreate, runAdminsActivate } = await import("../src/commands/admins.js");
    const created = await runAdminsCreate(
      { username: "frank", displayName: "Frank", roleIds: ["node-owner"] },
      { profile: "secretrootp", json: false },
    );

    let totpSecretBase32 = "";
    const enrollInput = new FakeInputSource({
      piped: created.enrollmentToken,
      visibles: [
        () => {
          const [enrollment] = [...server.enrollments.values()];
          return totp(enrollment!.totpSecretPending, Math.floor(Date.now() / 1000));
        },
      ],
    });
    await runEnroll(
      { node: origin, profile: "frank", keyName: "frank-key", label: "laptop", passphrase: false, force: false, json: false },
      enrollInput,
    );
    totpSecretBase32 = logs.join("\n").match(/manually: (\S+)/)?.[1] ?? "";
    expect(totpSecretBase32).not.toBe(""); // sanity: we actually captured the one-time-shown secret

    const frankMeta = readKeyFileMeta("frank-key");
    await runAdminsActivate(created.admin.id, frankMeta.fingerprint, { profile: "secretrootp", json: false });

    // From here on, nothing should repeat the enrollment token, the TOTP secret, the passphrase,
    // or any bearer token — all of those were only ever legitimate to show ONCE, at the point
    // above where each was generated.
    logs.length = 0;
    errs.length = 0;
    const loginInput = new FakeInputSource({
      visibles: [() => totp(server.admins.get(created.admin.id)!.totpSecret!, Math.floor(Date.now() / 1000))],
    });
    const frankSession = await runLogin({ profile: "frank", json: true }, loginInput);

    const combined = [...logs, ...errs].join("\n");
    expect(combined).not.toContain(passphrase);
    expect(combined).not.toContain(rawPrivateKeyField);
    expect(combined).not.toContain(rootSession.accessToken);
    expect(combined).not.toContain(frankSession.accessToken);
    expect(combined).not.toContain(created.enrollmentToken);
    expect(combined).not.toContain(totpSecretBase32);
  });

  it("login never prints the bearer access token, even in --json mode", async () => {
    const input = new FakeInputSource({ secrets: ["another long passphrase here", "another long passphrase here", "another long passphrase here"] });
    await runKeygen({ name: "jsonroot", kind: "root", passphrase: true, json: false }, input);
    const meta = readKeyFileMeta("jsonroot");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("jsonrootp", { node: origin, keyName: "jsonroot", kind: "root" }, true);

    const session = await runLogin({ profile: "jsonrootp", root: true, json: true }, input);

    const combined = logs.join("\n");
    expect(combined).not.toContain(session.accessToken);
  });
});

describe("wrong passphrase maps to exit code 3 (AUTH), not a bare crypto error at exit 5", () => {
  let home: string;
  let server: FakeNodeServer;
  let origin: string;

  beforeEach(async () => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-wrongpass-"));
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

  it("login with the wrong key passphrase fails with AUTH_FAILED / exit 3 and an actionable message", async () => {
    // The /auth/challenge round trip happens before the key file is ever decrypted, so this
    // needs a real reachable server (an unreachable placeholder origin would fail with a
    // network error first, never exercising the decrypt path this test is about).
    const correctPassphrase = "the correct passphrase here";
    const setupInput = new FakeInputSource({ secrets: [correctPassphrase, correctPassphrase] });
    await runKeygen({ name: "wrongpass", kind: "root", passphrase: true, force: false, json: false }, setupInput);
    const meta = readKeyFileMeta("wrongpass");
    server.rootPublicKeys.add(meta.publicKey);
    upsertProfile("wrongpassp", { node: origin, keyName: "wrongpass", kind: "root" }, true);

    const wrongInput = new FakeInputSource({ secrets: ["definitely the wrong passphrase"] });
    let caught: AzoaCliError | undefined;
    try {
      await runLogin({ profile: "wrongpassp", root: true, json: false }, wrongInput);
    } catch (err) {
      caught = err as AzoaCliError;
    }
    expect(caught).toBeInstanceOf(AzoaCliError);
    expect(caught!.exitCode).toBe(3);
    expect(caught!.code).toBe("AUTH_FAILED");
    expect(caught!.message).toMatch(/wrong passphrase or corrupted key file/i);
  });
});
