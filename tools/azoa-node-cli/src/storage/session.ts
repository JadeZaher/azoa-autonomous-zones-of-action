import { sessionPath } from "./paths.js";
import { readJsonFile, writeJsonFileSecure, deleteFileIfExists } from "./fsutil.js";

export interface StoredSession {
  accessToken: string;
  expiresAt: string;
  sid: string;
  principal: string;
  permissions: string[];
  authTime: string;
  admin?: { id: string; username: string; roles: string[] };
}

export function loadSession(profile: string): StoredSession | null {
  return readJsonFile<StoredSession>(sessionPath(profile));
}

export function saveSession(profile: string, session: StoredSession): void {
  writeJsonFileSecure(sessionPath(profile), session);
}

export function clearSession(profile: string): void {
  deleteFileIfExists(sessionPath(profile));
}

/** Step-up re-auth window mirrors the server's RecentNodeOperatorSession check (plan.md §3.4). */
export function isRecentAuth(authTime: string, windowMinutes = 10): boolean {
  const authMs = new Date(authTime).getTime();
  if (Number.isNaN(authMs)) return false;
  return Date.now() - authMs <= windowMinutes * 60_000;
}
