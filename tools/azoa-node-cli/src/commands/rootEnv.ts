import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { delimiter, join } from "node:path";
import { fingerprintOf } from "../crypto/keys.js";
import { fromBase64Url } from "../crypto/encoding.js";
import { AzoaCliError } from "../errors.js";
import { emit } from "../io/output.js";
import { getProfile } from "../storage/config.js";
import { readKeyFileMeta } from "../storage/keyFile.js";

export interface RootEnvOptions {
  profiles?: string[];
  addPublicKey?: string[];
  remove?: string;
  current?: string;
  currentRevision?: number;
  railway?: boolean;
  project?: string;
  environment?: string;
  service?: string;
  yes?: boolean;
  json: boolean;
}

/**
 * CONTRACT ASSUMPTION: `root env` has no wire contract with the node — it is
 * a local helper that renders the `NodeAdmin__RootPublicKeys` /
 * `NodeAdmin__RootKeyRevision` env lines. Without `--railway` it has no way
 * to know the value already deployed, so `--current`/`--current-revision`
 * let the operator (or a test) supply it.
 *
 * SECURITY (fixed after coordinator review of cd91ed55 — see notepad
 * issues.md): every value that reaches a spawned `railway` process (project/
 * environment/service ids, public keys, the computed key-list value) is
 * validated against a closed character class *before* any spawn, and the
 * binary is resolved and invoked directly — never through a shell — so a
 * malicious `--project`/`--remove`/etc. cannot inject shell metacharacters.
 */

// --- validation (exit 2 on any failure, before any spawn) ---------------

const PUBLIC_KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
const FINGERPRINT_PATTERN = /^[0-9a-f]{16}$/;
const RAILWAY_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

function assertValidPublicKey(key: string): void {
  if (!PUBLIC_KEY_PATTERN.test(key)) {
    throw AzoaCliError.usage(`Invalid public key format (expected 43 base64url chars): '${key}'`);
  }
  let decoded: Buffer;
  try {
    decoded = fromBase64Url(key);
  } catch {
    throw AzoaCliError.usage(`Public key is not valid base64url: '${key}'`);
  }
  if (decoded.length !== 32) {
    throw AzoaCliError.usage(`Public key must decode to exactly 32 bytes, got ${decoded.length}: '${key}'`);
  }
}

function assertValidRailwayId(label: string, value: string | undefined): asserts value is string {
  if (value === undefined || !RAILWAY_ID_PATTERN.test(value)) {
    throw AzoaCliError.usage(`Invalid ${label}: must match ${RAILWAY_ID_PATTERN} (got ${JSON.stringify(value)}).`);
  }
}

/**
 * Validates `--current-revision`, which names the ALREADY-DEPLOYED value, not the one about to
 * be rendered. 0 is the legitimate bootstrap state ("nothing deployed yet" — see README's own
 * walkthrough: `--current "" --current-revision 0`), so only negative/non-integer input is
 * rejected here. The server-side rule that a revision must be >= 1 once any key IS configured
 * (NodeAdminRuntime.cs) is enforced on the OUTPUT instead, below (join-report.md fix).
 */
function assertValidRevision(n: number | undefined): void {
  if (n === undefined) return;
  if (!Number.isInteger(n) || n < 0) {
    throw AzoaCliError.usage(`Invalid revision: must be a non-negative integer, got '${n}'.`);
  }
}

/** `--remove` accepts either a raw public key or its fingerprint; reject anything else. */
function assertValidRemoveTarget(value: string): void {
  if (!PUBLIC_KEY_PATTERN.test(value) && !FINGERPRINT_PATTERN.test(value)) {
    throw AzoaCliError.usage(`--remove must be a public key or a 16-hex-char fingerprint, got '${value}'.`);
  }
}

function matchesRemoveTarget(key: string, removeTarget: string): boolean {
  if (key === removeTarget) return true;
  if (!FINGERPRINT_PATTERN.test(removeTarget)) return false;
  try {
    const decoded = fromBase64Url(key);
    return decoded.length === 32 && fingerprintOf(decoded) === removeTarget;
  } catch {
    return false;
  }
}

// --- Railway binary resolution (never a shell) ---------------------------

export interface ResolvedRailwayExecutable {
  command: string;
  /** Extra argv entries to prepend, e.g. the shim's .js path when invoked via `process.execPath`. */
  prefixArgs: string[];
}

/**
 * Walks PATH looking for a real `railway`/`railway.exe` binary first. If only
 * the npm `@railway/cli` shim (`railway.cmd`) is present, resolves the real
 * binary npm installed beside it (`<dir>/node_modules/@railway/cli/bin/railway.exe`,
 * falling back to the bundled `railway.js` run via `process.execPath`) —
 * never spawns the `.cmd` shim itself, which would require a shell.
 */
export function resolveRailwayExecutable(env: NodeJS.ProcessEnv = process.env): ResolvedRailwayExecutable | null {
  const pathValue = env.PATH ?? env.Path ?? "";
  const dirs = pathValue.split(delimiter).filter(Boolean);

  const directName = process.platform === "win32" ? "railway.exe" : "railway";
  for (const dir of dirs) {
    const direct = join(dir, directName);
    if (existsSync(direct)) return { command: direct, prefixArgs: [] };
  }

  if (process.platform === "win32") {
    for (const dir of dirs) {
      if (!existsSync(join(dir, "railway.cmd"))) continue;
      const bundledExe = join(dir, "node_modules", "@railway", "cli", "bin", "railway.exe");
      if (existsSync(bundledExe)) return { command: bundledExe, prefixArgs: [] };
      const bundledJs = join(dir, "node_modules", "@railway", "cli", "bin", "railway.js");
      if (existsSync(bundledJs)) return { command: process.execPath, prefixArgs: [bundledJs] };
    }
  }

  return null;
}

// --- Railway adapter (injectable for tests) -------------------------------

export interface RailwayScope {
  project: string;
  environment: string;
  service: string;
}

export interface RailwayAdapter {
  listVariables(scope: RailwayScope): Record<string, string>;
  /** One variable per call — matches `railway variable set <KEY> --stdin ...`. */
  setVariable(key: string, value: string, scope: RailwayScope, options?: { skipDeploys?: boolean }): void;
}

export type SpawnSyncLike = (
  command: string,
  args: string[],
  options: { input?: string; encoding: "utf8" },
) => { status: number | null; stdout: string; stderr: string };

const realSpawn: SpawnSyncLike = (command, args, options) => spawnSync(command, args, options);

export function createRealRailwayAdapter(
  spawn: SpawnSyncLike = realSpawn,
  resolveExecutable: () => ResolvedRailwayExecutable | null = resolveRailwayExecutable,
): RailwayAdapter {
  function run(args: string[], input?: string): { status: number | null; stdout: string; stderr: string } {
    const resolved = resolveExecutable();
    if (!resolved) {
      throw AzoaCliError.network(
        "Could not find the Railway CLI: no railway.exe on PATH and no @railway/cli npm shim found either. " +
          "Install it (https://railway.app/cli) or put railway.exe on PATH, then re-run with --railway — " +
          "or apply the NodeAdmin__* lines above manually.",
      );
    }
    // No `shell` option anywhere: `command` is always resolved to a real executable (.exe or
    // `process.execPath` + a .js path), both directly spawnable without a command interpreter.
    const result = spawn(resolved.command, [...resolved.prefixArgs, ...args], { input, encoding: "utf8" });
    if (result.status !== 0) {
      throw AzoaCliError.network(`railway ${args.join(" ")} failed (exit ${result.status}): ${result.stderr || result.stdout}`);
    }
    return result;
  }

  return {
    listVariables(scope) {
      const result = run([
        "variable",
        "list",
        "--project",
        scope.project,
        "--environment",
        scope.environment,
        "--service",
        scope.service,
        "--json",
      ]);
      return JSON.parse(result.stdout) as Record<string, string>;
    },
    setVariable(key, value, scope, options) {
      const args = [
        "variable",
        "set",
        key,
        "--stdin",
        "--project",
        scope.project,
        "--environment",
        scope.environment,
        "--service",
        scope.service,
      ];
      if (options?.skipDeploys) args.push("--skip-deploys");
      // The value never touches argv — it goes to the child's stdin, so it can't be read from
      // `ps`/process listings and isn't subject to argv-length limits either.
      run(args, value);
    },
  };
}

// --- command ---------------------------------------------------------------

function parseKeyList(value: string | undefined): string[] {
  return (value ?? "")
    .split(",")
    .map((k) => k.trim())
    .filter(Boolean);
}

function keysForProfiles(names: string[]): string[] {
  return names.map((name) => {
    const profile = getProfile(name);
    if (profile.kind !== "root") {
      throw AzoaCliError.usage(`Profile '${name}' is kind '${profile.kind}', not root.`);
    }
    const key = readKeyFileMeta(profile.keyName).publicKey;
    assertValidPublicKey(key);
    return key;
  });
}

export function runRootEnv(options: RootEnvOptions, railway: RailwayAdapter = createRealRailwayAdapter()): void {
  for (const key of options.addPublicKey ?? []) assertValidPublicKey(key);
  if (options.remove !== undefined) assertValidRemoveTarget(options.remove);
  assertValidRevision(options.currentRevision);

  let scope: RailwayScope | undefined;
  if (options.railway) {
    assertValidRailwayId("--project", options.project);
    assertValidRailwayId("--environment", options.environment);
    assertValidRailwayId("--service", options.service);
    scope = { project: options.project, environment: options.environment, service: options.service };
  }

  let currentKeys: string[];
  let currentRevision: number;
  if (scope) {
    const vars = railway.listVariables(scope);
    currentKeys = parseKeyList(vars.NodeAdmin__RootPublicKeys);
    currentRevision = Number(vars.NodeAdmin__RootKeyRevision ?? "0");
    // M5: a garbage/missing env value silently becomes NaN under Number(...) — refuse rather
    // than let NaN propagate into the revision arithmetic below.
    if (!Number.isInteger(currentRevision) || currentRevision < 0) {
      throw AzoaCliError.network(
        `Read back an invalid NodeAdmin__RootKeyRevision from Railway: ${JSON.stringify(vars.NodeAdmin__RootKeyRevision)}.`,
      );
    }
  } else {
    currentKeys = parseKeyList(options.current);
    currentRevision = options.currentRevision ?? 0;
  }
  for (const key of currentKeys) assertValidPublicKey(key);

  const additions = [...(options.addPublicKey ?? []), ...keysForProfiles(options.profiles ?? [])];

  let nextKeys = [...new Set([...currentKeys, ...additions])];
  let nextRevision = currentRevision;
  if (options.remove) {
    const before = nextKeys.length;
    nextKeys = nextKeys.filter((k) => !matchesRemoveTarget(k, options.remove!));
    if (nextKeys.length !== before) nextRevision += 1;
  }

  // Bootstrap (join-report.md fix): going from zero configured keys to one or more must produce
  // a revision >= 1 — NodeAdminRuntime.cs disables root login outright if keys are configured
  // with revision 0/unset. Without this, rendering the very first root key (README's own
  // walkthrough, `--current "" --current-revision 0`) silently emitted NodeAdmin__RootPublicKeys
  // with no revision line at all, since nextRevision started equal to currentRevision (0) and
  // nothing else bumps it on a pure addition.
  if (nextKeys.length > 0 && nextRevision < 1) {
    nextRevision = 1;
  }

  const added = nextKeys.filter((k) => !currentKeys.includes(k));
  const removed = currentKeys.filter((k) => !nextKeys.includes(k));

  const lines = [`NodeAdmin__RootPublicKeys=${nextKeys.join(",")}`];
  if (nextRevision !== currentRevision) {
    lines.push(`NodeAdmin__RootKeyRevision=${nextRevision}`);
  }

  const nothingChanged = added.length === 0 && removed.length === 0 && nextRevision === currentRevision;

  if (scope) {
    if (nothingChanged) {
      emit(options.json, "Nothing to do — the computed value already matches what's deployed.", { changed: false, lines });
      return;
    }
    if (!options.yes) {
      emit(
        options.json,
        `Diff (not applied — pass --yes to confirm):\n+ added: ${added.join(", ") || "(none)"}\n- removed: ${removed.join(", ") || "(none)"}\n${lines.join("\n")}`,
        { added, removed, lines },
      );
      return;
    }
    // M5: for a removal, the revision bump must land (skip-deploys) BEFORE the key list change
    // that actually deploys — a deploy carrying the new key list under the OLD revision watermark
    // would be weaker than either change landing alone. Pure additions have no revision bump, so
    // the key list is simply the only (and therefore last) call.
    const toSet: Array<[string, string]> = [];
    if (nextRevision !== currentRevision) toSet.push(["NodeAdmin__RootKeyRevision", String(nextRevision)]);
    toSet.push(["NodeAdmin__RootPublicKeys", nextKeys.join(",")]);
    toSet.forEach(([key, value], index) => {
      railway.setVariable(key, value, scope!, { skipDeploys: index < toSet.length - 1 });
    });
  }

  emit(options.json, lines.join("\n"), { lines, added, removed, nextRevision });
}
