import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { generateEd25519KeyPair } from "../src/crypto/keys.js";
import { AzoaCliError } from "../src/errors.js";
import { writeKeyFile } from "../src/storage/keyFile.js";
import { guardKeyOverwrite } from "../src/commands/keyGuards.js";
import { FakeInputSource } from "./fixtures/fakeInput.js";

describe("guardKeyOverwrite (H3: refuse to clobber an existing key file)", () => {
  let home: string;

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-keyguard-"));
    process.env.AZOA_HOME = home;
  });

  afterEach(() => {
    delete process.env.AZOA_HOME;
    rmSync(home, { recursive: true, force: true });
  });

  it("is a no-op when no key exists yet", async () => {
    await expect(guardKeyOverwrite("fresh-name", "admin", false, new FakeInputSource())).resolves.toBeUndefined();
  });

  it("refuses an existing key without --force", async () => {
    await writeKeyFile("existing", generateEd25519KeyPair(), "admin", null);
    await expect(guardKeyOverwrite("existing", "admin", false, new FakeInputSource())).rejects.toThrow(AzoaCliError);
    await expect(guardKeyOverwrite("existing", "admin", false, new FakeInputSource())).rejects.toThrow(/already exists/);
  });

  it("allows an existing ADMIN key to be overwritten with --force and no extra confirmation", async () => {
    await writeKeyFile("existing-admin", generateEd25519KeyPair(), "admin", null);
    await expect(guardKeyOverwrite("existing-admin", "admin", true, new FakeInputSource())).resolves.toBeUndefined();
  });

  it("requires typing the key name to confirm overwriting a ROOT key, even with --force", async () => {
    await writeKeyFile("existing-root", generateEd25519KeyPair(), "root", "whatever-passphrase");

    await expect(
      guardKeyOverwrite("existing-root", "root", true, new FakeInputSource({ visibles: ["wrong-name"] })),
    ).rejects.toThrow(/did not match/);

    await expect(
      guardKeyOverwrite("existing-root", "root", true, new FakeInputSource({ visibles: ["existing-root"] })),
    ).resolves.toBeUndefined();
  });
});
