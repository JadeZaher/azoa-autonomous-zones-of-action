import { resolveProfile, requireSession, httpClientFor } from "../context.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";

export interface SessionsListOptions {
  profile?: string;
  admin?: string;
  json: boolean;
}

interface SessionRow {
  /** Opaque session-list/revoke id — never the token's real sid (lane A1 security fix M-1). */
  handle: string;
  principal: string;
  authTime: string;
  amr: string[];
  expiresAt: string;
  revokedAt?: string;
  ip: string;
  current: boolean;
}

/**
 * `GET /api/node-admin/sessions` filters by the full `principal` string
 * (`admin:<id>` or `root:<fingerprint>`), not a bare admin id — confirmed
 * against `NodeAdminManager.ListSessionsAsync` (lane A1 f4d5a77e). `--admin`
 * stays a convenience flag: a bare id is auto-prefixed with `admin:`, and a
 * value that already names a principal (contains `:`) passes through as-is
 * so `--admin root:<fp>` still works.
 */
function toPrincipal(admin: string): string {
  return admin.includes(":") ? admin : `admin:${admin}`;
}

export async function runSessionsList(options: SessionsListOptions): Promise<SessionRow[]> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  const path = options.admin
    ? `/api/node-admin/sessions?principal=${encodeURIComponent(toPrincipal(options.admin))}`
    : "/api/node-admin/sessions";
  const { data } = await client.call<SessionRow[]>("GET", path);
  emit(
    options.json,
    data.map((s) => `${s.current ? "*" : " "} ${s.handle}\t${s.principal}\t${s.authTime}\t${s.revokedAt ? "revoked" : "active"}`).join("\n") || "(no sessions)",
    data,
  );
  return data;
}

export async function runSessionRevoke(
  handle: string,
  options: { profile?: string; json: boolean },
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(resolved, "POST", `/api/node-admin/sessions/${encodeURIComponent(handle)}/revoke`, undefined, input);
  emit(options.json, `Session ${handle} revoked.`, { handle });
}
