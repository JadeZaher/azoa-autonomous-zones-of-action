import { AzoaCliError } from "../errors.js";
import { configPath } from "./paths.js";
import { readJsonFile, writeJsonFileSecure } from "./fsutil.js";

export type ProfileKind = "root" | "admin";

export interface Profile {
  /** Node's public origin, e.g. "https://node.example.test". Pinned for audience checks. */
  node: string;
  /** Key file name (without directory) under ~/.azoa/keys/. */
  keyName: string;
  kind: ProfileKind;
  adminId?: string;
  username?: string;
}

export interface AzoaConfig {
  profiles: Record<string, Profile>;
  currentProfile?: string;
}

const EMPTY_CONFIG: AzoaConfig = { profiles: {} };

export function loadConfig(): AzoaConfig {
  return readJsonFile<AzoaConfig>(configPath()) ?? { ...EMPTY_CONFIG, profiles: {} };
}

export function saveConfig(config: AzoaConfig): void {
  writeJsonFileSecure(configPath(), config);
}

export function upsertProfile(name: string, profile: Profile, makeCurrent = false): AzoaConfig {
  const config = loadConfig();
  config.profiles[name] = profile;
  if (makeCurrent || !config.currentProfile) {
    config.currentProfile = name;
  }
  saveConfig(config);
  return config;
}

export function resolveProfileName(explicit: string | undefined): string {
  const config = loadConfig();
  const name = explicit ?? config.currentProfile;
  if (!name) {
    throw AzoaCliError.usage("No profile specified and no current profile set. Run 'azoa-node profile add' first.");
  }
  return name;
}

export function getProfile(name: string): Profile {
  const config = loadConfig();
  const profile = config.profiles[name];
  if (!profile) {
    throw AzoaCliError.usage(`Unknown profile '${name}'. Run 'azoa-node profile list'.`);
  }
  return profile;
}
