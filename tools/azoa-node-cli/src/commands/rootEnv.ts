import { spawnSync } from "node:child_process";
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
 * let the operator (or a test) supply it; with `--railway` the adapter below
 * fetches/sets it instead. Railway CLI flag syntax is best-effort (not
 * verified against a live `railway` binary in this environment) — see
 * RailwayAdapter.
 */
export interface RailwayAdapter {
  listVariables(args: { project?: string; environment?: string; service?: string }): Promise<Record<string, string>>;
  setVariables(
    vars: Record<string, string>,
    args: { project?: string; environment?: string; service?: string },
  ): Promise<void>;
}

function resolveRailwayBinary(): string {
  // Windows ships the Railway CLI as a .cmd/.ps1 shim; spawn through the shell so PATH resolution works.
  return process.platform === "win32" ? "railway.cmd" : "railway";
}

export function createRealRailwayAdapter(): RailwayAdapter {
  return {
    async listVariables(args) {
      const flags: string[] = ["variables", "--json"];
      if (args.project) flags.push("--project", args.project);
      if (args.environment) flags.push("--environment", args.environment);
      if (args.service) flags.push("--service", args.service);
      const result = spawnSync(resolveRailwayBinary(), flags, { encoding: "utf8", shell: true });
      if (result.status !== 0) {
        throw AzoaCliError.network(`railway variables --json failed: ${result.stderr || result.stdout}`);
      }
      return JSON.parse(result.stdout) as Record<string, string>;
    },
    async setVariables(vars, args) {
      const flags: string[] = ["variables"];
      if (args.project) flags.push("--project", args.project);
      if (args.environment) flags.push("--environment", args.environment);
      if (args.service) flags.push("--service", args.service);
      for (const [key, value] of Object.entries(vars)) {
        flags.push("--set", `${key}=${value}`);
      }
      const result = spawnSync(resolveRailwayBinary(), flags, { encoding: "utf8", shell: true });
      if (result.status !== 0) {
        throw AzoaCliError.network(`railway variables --set failed: ${result.stderr || result.stdout}`);
      }
    },
  };
}

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
    return readKeyFileMeta(profile.keyName).publicKey;
  });
}

export async function runRootEnv(
  options: RootEnvOptions,
  railway: RailwayAdapter = createRealRailwayAdapter(),
): Promise<void> {
  let currentKeys: string[];
  let currentRevision: number;

  if (options.railway) {
    const vars = await railway.listVariables(options);
    currentKeys = parseKeyList(vars.NodeAdmin__RootPublicKeys);
    currentRevision = Number(vars.NodeAdmin__RootKeyRevision ?? "0");
  } else {
    currentKeys = parseKeyList(options.current);
    currentRevision = options.currentRevision ?? 0;
  }

  const additions = [
    ...(options.addPublicKey ?? []),
    ...keysForProfiles(options.profiles ?? []),
  ];

  let nextKeys = [...new Set([...currentKeys, ...additions])];
  let nextRevision = currentRevision;
  if (options.remove) {
    const before = nextKeys.length;
    nextKeys = nextKeys.filter((k) => {
      // caller passes a fingerprint for --remove; match by recomputing is the caller's job elsewhere,
      // so here we accept either a raw public key or drop any key literal match.
      return k !== options.remove;
    });
    if (nextKeys.length !== before) nextRevision += 1;
  }

  const added = nextKeys.filter((k) => !currentKeys.includes(k));
  const removed = currentKeys.filter((k) => !nextKeys.includes(k));

  const lines = [`NodeAdmin__RootPublicKeys=${nextKeys.join(",")}`];
  if (nextRevision !== currentRevision) {
    lines.push(`NodeAdmin__RootKeyRevision=${nextRevision}`);
  }

  if (options.railway) {
    if (!options.yes) {
      emit(
        options.json,
        `Diff (not applied — pass --yes to confirm):\n+ added: ${added.join(", ") || "(none)"}\n- removed: ${removed.join(", ") || "(none)"}\n${lines.join("\n")}`,
        { added, removed, lines },
      );
      return;
    }
    const toSet: Record<string, string> = { NodeAdmin__RootPublicKeys: nextKeys.join(",") };
    if (nextRevision !== currentRevision) toSet.NodeAdmin__RootKeyRevision = String(nextRevision);
    await railway.setVariables(toSet, options);
  }

  emit(options.json, lines.join("\n"), { lines, added, removed, nextRevision });
}
