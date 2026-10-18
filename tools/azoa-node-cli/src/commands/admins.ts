import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";

interface AdminDto {
  id: string;
  username: string;
  displayName: string;
  status: string;
  roles: { id: string; name: string }[];
  permissions: string[];
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  keys: { fingerprint: string; label: string; createdAt: string; revokedAt?: string }[];
  mfaEnrolled: boolean;
}

interface CreateAdminResponse {
  admin: AdminDto;
  enrollmentToken: string;
  enrollmentExpiresAt: string;
}

export interface CommonOptions {
  profile?: string;
  json: boolean;
}

export async function runAdminsList(options: CommonOptions): Promise<AdminDto[]> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<AdminDto[]>("GET", "/api/node-admin/admins");
  emit(options.json, data.map((a) => `${a.id}\t${a.username}\t${a.status}\t${a.roles.map((r) => r.name).join(",")}`).join("\n") || "(no admins)", data);
  return data;
}

export async function runAdminsShow(id: string, options: CommonOptions): Promise<AdminDto> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<AdminDto>("GET", `/api/node-admin/admins/${encodeURIComponent(id)}`);
  emit(options.json, JSON.stringify(data, null, 2), data);
  return data;
}

export async function runAdminsCreate(
  args: { username: string; displayName: string; roleIds: string[] },
  options: CommonOptions,
): Promise<CreateAdminResponse> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<CreateAdminResponse>("POST", "/api/node-admin/admins", {
    username: args.username,
    displayName: args.displayName,
    roleIds: args.roleIds,
  });
  emit(
    options.json,
    `Created admin '${data.admin.username}' (${data.admin.id}).\n` +
      `Enrollment token (shown once, expires ${data.enrollmentExpiresAt}):\n${data.enrollmentToken}`,
    data,
  );
  return data;
}

async function simpleAdminAction(id: string, action: string, options: CommonOptions, body?: unknown): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call("POST", `/api/node-admin/admins/${encodeURIComponent(id)}/${action}`, body);
  emit(options.json, `admins/${id}: ${action} ok.`, { id, action });
}

export const runAdminsSuspend = (id: string, options: CommonOptions) => simpleAdminAction(id, "suspend", options);
export const runAdminsReactivate = (id: string, options: CommonOptions) => simpleAdminAction(id, "reactivate", options);
export const runAdminsRevoke = (id: string, options: CommonOptions) => simpleAdminAction(id, "revoke", options);
export const runAdminsRevokeSessions = (id: string, options: CommonOptions) =>
  simpleAdminAction(id, "revoke-sessions", options);

export async function runAdminsActivate(
  id: string,
  fingerprint: string,
  options: CommonOptions,
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call("POST", `/api/node-admin/admins/${encodeURIComponent(id)}/activate`, { fingerprint });
  emit(options.json, `admins/${id}: activated with fingerprint ${fingerprint}.`, { id, fingerprint });
}

export async function runAdminsSetRoles(id: string, roleIds: string[], options: CommonOptions): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call("PUT", `/api/node-admin/admins/${encodeURIComponent(id)}/roles`, { roleIds });
  emit(options.json, `admins/${id}: roles set to [${roleIds.join(", ")}].`, { id, roleIds });
}
