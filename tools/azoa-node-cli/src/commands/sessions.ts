import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";

export interface SessionsListOptions {
  profile?: string;
  admin?: string;
  json: boolean;
}

interface SessionRow {
  sid: string;
  principal: string;
  authTime: string;
  amr: string[];
  expiresAt: string;
  revokedAt?: string;
  ip: string;
  current: boolean;
}

export async function runSessionsList(options: SessionsListOptions): Promise<SessionRow[]> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  const path = options.admin ? `/api/node-admin/sessions?adminId=${encodeURIComponent(options.admin)}` : "/api/node-admin/sessions";
  const { data } = await client.call<SessionRow[]>("GET", path);
  emit(
    options.json,
    data.map((s) => `${s.current ? "*" : " "} ${s.sid}\t${s.principal}\t${s.authTime}\t${s.revokedAt ? "revoked" : "active"}`).join("\n") || "(no sessions)",
    data,
  );
  return data;
}

export async function runSessionRevoke(sid: string, options: { profile?: string; json: boolean }): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  await client.call("POST", `/api/node-admin/sessions/${encodeURIComponent(sid)}/revoke`);
  emit(options.json, `Session ${sid} revoked.`, { sid });
}
