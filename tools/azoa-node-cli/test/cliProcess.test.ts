import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

const CLI_PATH = join(dirname(fileURLToPath(import.meta.url)), "..", "dist", "cli.js");

describe("command-line parser exit codes", () => {
  it.each([
    ["--unknown-flag"],
    ["keygen", "--unknown-flag"],
    ["keygen"],
    ["api", "GET"],
    ["unknown-command"],
    ["whoami", "--bogus"],
    ["whoami", "extra"],
    ["root", "env", "--bogus"],
  ])("rejects invalid arguments with usage exit 2: %j", (...args: string[]) => {
    const result = spawnSync(process.execPath, [CLI_PATH, ...args], { encoding: "utf8", input: "", timeout: 10_000 });
    expect(result.error).toBeUndefined();
    expect(result.status).toBe(2);
    expect(result.stderr).toMatch(/error:/);
    expect(result.stderr).not.toMatch(/CommanderError|node:internal|at Command\./);
    expect(result.stdout).toBe("");
  });

  it("prints help successfully", () => {
    const result = spawnSync(process.execPath, [CLI_PATH, "--help"], { encoding: "utf8", timeout: 10_000 });
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("Usage:");
    expect(result.stderr).toBe("");
  });
});

/**
 * Real-process coverage for the join-report.md FIFO stdin fix: `readSecret`/`readPipedStdin`
 * used to slurp the ENTIRE piped stream on their first call, starving every later prompt in the
 * same invocation. This was never caught by the rest of this suite because it drives prompts
 * through a `FakeInputSource`, never real stdin — so this test spawns the actual built CLI
 * binary with a real pipe, the only way to reproduce the bug this fixes.
 */
describe("real piped stdin across multiple sequential prompts (join-report.md fix)", () => {
  let home: string;

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), "azoa-cli-process-"));
  });

  afterEach(() => {
    rmSync(home, { recursive: true, force: true });
  });

  it("keygen with a piped passphrase AND confirmation both land correctly (not just the first read)", () => {
    const passphrase = "a genuinely piped passphrase";
    const result = spawnSync(process.execPath, [CLI_PATH, "keygen", "--name", "piped", "--kind", "admin", "--json"], {
      cwd: home,
      env: { ...process.env, AZOA_HOME: home },
      input: `${passphrase}\n${passphrase}\n`,
      encoding: "utf8",
    });

    expect(result.status).toBe(0);
    const parsed = JSON.parse(result.stdout);
    expect(parsed.fingerprint).toMatch(/^[0-9a-f]{16}$/);

    // Prove the SECOND line (the confirmation) was actually consumed, not left in the pipe or
    // silently read as empty: a mismatched confirmation must fail, which only happens if both
    // reads worked.
    const mismatchResult = spawnSync(process.execPath, [CLI_PATH, "keygen", "--name", "piped2", "--kind", "admin", "--json"], {
      cwd: home,
      env: { ...process.env, AZOA_HOME: home },
      input: `${passphrase}\nsomething-completely-different-here\n`,
      encoding: "utf8",
    });
    expect(mismatchResult.status).toBe(2); // usage error: passphrases did not match
    expect(mismatchResult.stderr).toMatch(/did not match/);
  });
});
