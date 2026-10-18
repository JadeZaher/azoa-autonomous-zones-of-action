import { AzoaCliError } from "../errors.js";
import { emit } from "../io/output.js";
import { loadConfig, upsertProfile, type ProfileKind } from "../storage/config.js";
import { assertValidName } from "../validation.js";

export interface ProfileAddOptions {
  name: string;
  node: string;
  kind: ProfileKind;
  keyName: string;
  adminId?: string;
  username?: string;
  use: boolean;
  json: boolean;
}

export function runProfileAdd(options: ProfileAddOptions): void {
  assertValidName(options.name, "profile name");
  assertValidName(options.keyName, "--key-name");
  if (!/^https?:\/\//i.test(options.node)) {
    throw AzoaCliError.usage(`--origin must be a full origin (https://host[:port]), got '${options.node}'.`);
  }
  upsertProfile(
    options.name,
    {
      node: options.node.replace(/\/+$/, ""),
      keyName: options.keyName,
      kind: options.kind,
      adminId: options.adminId,
      username: options.username,
    },
    options.use,
  );
  emit(options.json, `Profile '${options.name}' saved.`, { name: options.name });
}

export function runProfileList(options: { json: boolean }): void {
  const config = loadConfig();
  emit(
    options.json,
    Object.entries(config.profiles)
      .map(([name, p]) => `${name === config.currentProfile ? "*" : " "} ${name}\t${p.kind}\t${p.node}`)
      .join("\n") || "(no profiles)",
    config,
  );
}

export function runProfileUse(name: string, options: { json: boolean }): void {
  const config = loadConfig();
  if (!config.profiles[name]) {
    throw AzoaCliError.usage(`Unknown profile '${name}'.`);
  }
  config.currentProfile = name;
  upsertProfile(name, config.profiles[name]!, true);
  emit(options.json, `Current profile is now '${name}'.`, { currentProfile: name });
}
