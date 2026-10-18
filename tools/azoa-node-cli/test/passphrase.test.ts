import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { generateEd25519KeyPair } from "../src/crypto/keys.js";
import { readKeyFile, writeKeyFile, keyFileIsEncrypted } from "../src/storage/keyFile.js";

describe("encrypted key file round-trip (real scrypt + AES-256-GCM)", () => {
  let home: string;

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-test-"));
    process.env.AZOA_HOME = home;
  });

  afterEach(() => {
    delete process.env.AZOA_HOME;
    rmSync(home, { recursive: true, force: true });
  });

  it("decrypts with the correct passphrase and signs identically to the original key", async () => {
    const original = generateEd25519KeyPair();
    await writeKeyFile("root", original, "root", "correct horse battery staple");

    expect(keyFileIsEncrypted("root")).toBe(true);

    const recovered = await readKeyFile("root", "correct horse battery staple");
    expect(recovered.publicKeyB64Url).toBe(original.publicKeyB64Url);
    expect(recovered.fingerprint).toBe(original.fingerprint);

    const { signMessage, verifySignature } = await import("../src/crypto/keys.js");
    const signature = signMessage(recovered.privateKey, "round-trip check");
    expect(verifySignature(recovered.publicKeyB64Url, "round-trip check", signature)).toBe(true);
  });

  it("fails closed on the wrong passphrase (AES-GCM auth tag mismatch, not silent corruption)", async () => {
    const original = generateEd25519KeyPair();
    await writeKeyFile("root", original, "root", "correct horse battery staple");

    await expect(readKeyFile("root", "wrong passphrase entirely")).rejects.toThrow();
  });

  it("stores admin keys unencrypted when no passphrase is given", async () => {
    const original = generateEd25519KeyPair();
    await writeKeyFile("admin1", original, "admin", null);
    expect(keyFileIsEncrypted("admin1")).toBe(false);
    const recovered = await readKeyFile("admin1", null);
    expect(recovered.fingerprint).toBe(original.fingerprint);
  });
});
