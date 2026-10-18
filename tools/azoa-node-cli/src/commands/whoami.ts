import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";

export interface WhoamiOptions {
  profile?: string;
  json: boolean;
}

interface MeResponse {
  principal: string;
  admin?: { id: string; username: string; status: string; roles: unknown[] };
  root?: { fingerprint: string };
  permissions: string[];
  session: { sid: string; authTime: string; expiresAt: string };
}

export async function runWhoami(options: WhoamiOptions): Promise<MeResponse> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  const { data } = await client.call<MeResponse>("GET", "/api/node-admin/me");
  emit(options.json, `${data.principal} — ${data.permissions.length} permissions.`, data);
  return data;
}
