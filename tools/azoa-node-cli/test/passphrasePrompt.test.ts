import { afterEach, describe, expect, it } from "vitest";
import { AzoaCliError } from "../src/errors.js";
import { promptNewPassphrase } from "../src/io/passphrase.js";
import { FakeInputSource } from "./fixtures/fakeInput.js";

describe("promptNewPassphrase (M3: one shared rule everywhere a new key's passphrase is collected)", () => {
  afterEach(() => {
    delete process.env.AZOA_KEY_PASSPHRASE;
  });

  it("confirms, accepting a matching pair >= 12 chars", async () => {
    const input = new FakeInputSource({ secrets: ["correct horse battery", "correct horse battery"] });
    await expect(promptNewPassphrase(input)).resolves.toBe("correct horse battery");
  });

  it("refuses a mismatched confirmation", async () => {
    const input = new FakeInputSource({ secrets: ["correct horse battery", "something else entirely"] });
    await expect(promptNewPassphrase(input)).rejects.toThrow(/did not match/);
  });

  it("refuses an empty passphrase", async () => {
    const input = new FakeInputSource({ secrets: ["", ""] });
    await expect(promptNewPassphrase(input)).rejects.toThrow(/cannot be empty/);
  });

  it("refuses a passphrase shorter than 12 characters", async () => {
    const input = new FakeInputSource({ secrets: ["short1234", "short1234"] });
    await expect(promptNewPassphrase(input)).rejects.toThrow(/at least 12 characters/);
  });

  it("prefers AZOA_KEY_PASSPHRASE over any prompt when set", async () => {
    process.env.AZOA_KEY_PASSPHRASE = "env supplied passphrase";
    const input = new FakeInputSource(); // no scripted secrets at all — would throw if actually read
    await expect(promptNewPassphrase(input)).resolves.toBe("env supplied passphrase");
  });

  it("refuses a too-short AZOA_KEY_PASSPHRASE instead of silently accepting it", async () => {
    process.env.AZOA_KEY_PASSPHRASE = "short";
    const input = new FakeInputSource();
    await expect(promptNewPassphrase(input)).rejects.toThrow(/AZOA_KEY_PASSPHRASE must be at least/);
  });

  it("an empty read (e.g. stdin ran out of piped lines) fails with a message pointing at AZOA_KEY_PASSPHRASE, not a hang", async () => {
    // src/io/input.ts's real FIFO stdin reader resolves a read with "" at EOF rather than hanging
    // forever when a script piped fewer lines than prompts issued (join-report.md fix); this
    // proves THAT shape of empty input still fails clearly here, without any separate
    // "did we already use stdin" tracking — the empty-passphrase check alone is enough.
    const input = new FakeInputSource({ secrets: [""] });
    await expect(promptNewPassphrase(input)).rejects.toThrow(/AZOA_KEY_PASSPHRASE/);
  });
});
