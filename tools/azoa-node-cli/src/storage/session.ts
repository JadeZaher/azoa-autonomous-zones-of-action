import { sessionPath } from "./paths.js";
import { readJsonFile, writeJsonFileSecure, deleteFileIfExists } from "./fsutil.js";

export interface StoredSession {
  accessToken: string;
  expiresAt: string;
  /** Opaque session-list/revoke id (lane A1 security fix M-1) — never the token's real sid. */
  handle: string;
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
