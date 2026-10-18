import { homedir } from "node:os";
import { join } from "node:path";

/** `AZOA_HOME` override lets tests point at a temp dir instead of the real `~/.azoa`. */
export function azoaHome(): string {
  return process.env.AZOA_HOME ?? join(homedir(), ".azoa");
}

export function configPath(): string {
  return join(azoaHome(), "config.json");
}

export function keysDir(): string {
  return join(azoaHome(), "keys");
}

export function keyPath(name: string): string {
  return join(keysDir(), `${name}.key`);
}

export function sessionsDir(): string {
  return join(azoaHome(), "sessions");
}

export function sessionPath(profile: string): string {
  return join(sessionsDir(), `${profile}.json`);
}

export function auditPinsDir(): string {
  return join(azoaHome(), "audit-pins");
}

export function auditPinPath(profile: string): string {
  return join(auditPinsDir(), `${profile}.json`);
}

/** Heuristic OneDrive-sync warning (plan.md §8 CLI binding). */
export function isUnderOneDrive(path: string): boolean {
  return /onedrive/i.test(path);
}
