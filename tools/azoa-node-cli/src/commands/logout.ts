import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";
import { clearSession } from "../storage/session.js";

export interface LogoutOptions {
  profile?: string;
  json: boolean;
}

export async function runLogout(options: LogoutOptions): Promise<void> {
  const resolved = resolveProfile(options.profile);
  const session = requireSession(resolved);
  const client = httpClientFor(resolved);
  try {
    await client.call("POST", "/api/node-admin/auth/revoke");
  } finally {
    clearSession(resolved.name);
  }
  emit(options.json, `Logged out '${resolved.name}' (${session.principal}).`, { profile: resolved.name });
}
