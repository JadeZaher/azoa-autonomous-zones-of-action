import { describe, expect, it, vi } from "vitest";
import { AzoaCliError } from "../src/errors.js";
import { createRealRailwayAdapter, runRootEnv, type SpawnSyncLike } from "../src/commands/rootEnv.js";

// Node builtin exports are non-configurable, so `vi.spyOn(childProcess, "spawnSync")` throws
// ("Cannot redefine property"). `vi.mock` intercepts the module at the registry level instead —
// hoisted above this file's imports, so `rootEnv.ts`'s own `import { spawnSync } from
// "node:child_process"` resolves to this mock too. Every other test in this file injects its own
// fake `spawn`/`resolveExecutable`, so it never touches this mock.
vi.mock("node:child_process", () => ({ spawnSync: vi.fn() }));

interface RecordedCall {
  command: string;
  args: string[];
  input?: string;
}

/** Fake spawn: records every call, answers `variable list` with canned JSON, `set` with an empty success. */
function buildFakeSpawn(listResponse: Record<string, string>): { spawn: SpawnSyncLike; calls: RecordedCall[] } {
  const calls: RecordedCall[] = [];
  const spawn: SpawnSyncLike = (command, args, options) => {
    calls.push({ command, args, input: options.input });
    if (args[0] === "variable" && args[1] === "list") {
      return { status: 0, stdout: JSON.stringify(listResponse), stderr: "" };
    }
    return { status: 0, stdout: "", stderr: "" };
  };
  return { spawn, calls };
}

const FAKE_EXECUTABLE = { command: "C:\\fake\\railway.exe", prefixArgs: [] };

// Two real Ed25519 public keys (b64url, 43 chars, decode to 32 bytes) used as fixture root keys.
const KEY_A = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
const FINGERPRINT_A = "21fe31dfa154a261";
const KEY_B = "HlodJLV9Kimzd4gu5t3dmY9o0eaZ3D6gdAHdQ1Js6cQ";

describe("root env --railway: no shell, strict validation before any spawn", () => {
  it.each([
    ["x & calc", "project"],
    ["$(touch pwned)", "project"],
    ["a;b", "project"],
    ["x & calc", "environment"],
    ["x & calc", "service"],
  ])("rejects a malicious %s as --%s with zero spawns", (maliciousValue, field) => {
    const { spawn, calls } = buildFakeSpawn({});
    const base = { project: "proj1", environment: "env1", service: "svc1" };
    const options = { railway: true as const, yes: true, json: false, ...base, [field]: maliciousValue };

    expect(() => runRootEnv(options, createRealRailwayAdapter(spawn, () => FAKE_EXECUTABLE))).toThrow(AzoaCliError);
    expect(() => runRootEnv(options, createRealRailwayAdapter(spawn, () => FAKE_EXECUTABLE))).toThrow(/Invalid (--)?project|environment|service/i);
    expect(calls.length).toBe(0);
  });

  it("rejects a malicious --remove with zero spawns", () => {
    const { spawn, calls } = buildFakeSpawn({});
    expect(() =>
      runRootEnv(
        { railway: true, project: "proj1", environment: "env1", service: "svc1", remove: "x & calc", yes: true, json: false },
        createRealRailwayAdapter(spawn, () => FAKE_EXECUTABLE),
      ),
    ).toThrow(AzoaCliError);
    expect(calls.length).toBe(0);
  });

  it("rejects a malformed --add-public-key with zero spawns", () => {
    const { spawn, calls } = buildFakeSpawn({});
    expect(() =>
      runRootEnv(
        { railway: true, project: "proj1", environment: "env1", service: "svc1", addPublicKey: ["a;b"], yes: true, json: false },
        createRealRailwayAdapter(spawn, () => FAKE_EXECUTABLE),
      ),
    ).toThrow(AzoaCliError);
    expect(calls.length).toBe(0);
  });

  it("produces the exact argv arrays for a valid removal — list, then --skip-deploys on the key-list set, no --skip-deploys on the final (revision) set", () => {
    const { spawn, calls } = buildFakeSpawn({
      NodeAdmin__RootPublicKeys: `${KEY_A},${KEY_B}`,
      NodeAdmin__RootKeyRevision: "3",
    });

    runRootEnv(
      {
        railway: true,
        project: "proj1",
        environment: "env1",
        service: "svc1",
        remove: FINGERPRINT_A, // remove by fingerprint, not the raw key
        yes: true,
        json: false,
      },
      createRealRailwayAdapter(spawn, () => FAKE_EXECUTABLE),
    );

    expect(calls).toHaveLength(3);

    expect(calls[0]).toEqual({
      command: "C:\\fake\\railway.exe",
      args: ["variable", "list", "--project", "proj1", "--environment", "env1", "--service", "svc1", "--json"],
      input: undefined,
    });

    expect(calls[1]).toEqual({
      command: "C:\\fake\\railway.exe",
      args: [
        "variable",
        "set",
        "NodeAdmin__RootPublicKeys",
        "--stdin",
        "--project",
        "proj1",
        "--environment",
        "env1",
        "--service",
        "svc1",
        "--skip-deploys",
      ],
      input: KEY_B, // KEY_A was removed; the value goes over stdin, never argv
    });

    expect(calls[2]).toEqual({
      command: "C:\\fake\\railway.exe",
      args: [
        "variable",
        "set",
        "NodeAdmin__RootKeyRevision",
        "--stdin",
        "--project",
        "proj1",
        "--environment",
        "env1",
        "--service",
        "svc1",
      ],
      input: "4",
    });

    // No call ever carries a `shell` key at all — the fake spawn's own type signature has none,
    // and the real adapter (exercised below) never adds one either.
    for (const call of calls) {
      expect(call).not.toHaveProperty("shell");
    }
  });

  it("fails cleanly (and still prints the computed lines) when the railway binary cannot be resolved", () => {
    const { spawn, calls } = buildFakeSpawn({});
    expect(() =>
      runRootEnv(
        { railway: true, project: "proj1", environment: "env1", service: "svc1", yes: true, json: false },
        createRealRailwayAdapter(spawn, () => null),
      ),
    ).toThrow(/Could not find the Railway CLI/);
    expect(calls.length).toBe(0);
  });
});

describe("root env: real spawnSync is never invoked with shell:true", () => {
  it("the default adapter's spawn wrapper passes no `shell` option to node:child_process.spawnSync", async () => {
    const { spawnSync } = await import("node:child_process");
    const mockSpawnSync = spawnSync as unknown as ReturnType<typeof vi.fn>;
    mockSpawnSync.mockReturnValue({ status: 0, stdout: JSON.stringify({}), stderr: "" });

    const adapter = createRealRailwayAdapter(undefined, () => FAKE_EXECUTABLE);
    adapter.listVariables({ project: "p", environment: "e", service: "s" });

    expect(mockSpawnSync).toHaveBeenCalledTimes(1);
    const optionsArg = mockSpawnSync.mock.calls[0]![2] as Record<string, unknown>;
    expect(optionsArg).not.toHaveProperty("shell");

    mockSpawnSync.mockReset();
  });
});
