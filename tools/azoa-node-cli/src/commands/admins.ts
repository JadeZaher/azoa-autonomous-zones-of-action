import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";

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

/** `NodeAdminReenrollmentView` (lane A1 M-2): shared by `mfa reset` and `PUT .../roles`. */
export interface ReenrollmentResponse {
  admin: AdminDto;
  enrollmentToken: string | null;
  enrollmentExpiresAt: string | null;
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
  input: InputSource = createRealInputSource(),
): Promise<CreateAdminResponse> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const data = await mutateWithStepUpRetry<CreateAdminResponse>(
    resolved,
    "POST",
    "/api/node-admin/admins",
    { username: args.username, displayName: args.displayName, roleIds: args.roleIds },
    input,
  );
  emit(
    options.json,
    `Created admin '${data.admin.username}' (${data.admin.id}).\n` +
      `Enrollment token (shown once, expires ${data.enrollmentExpiresAt}):\n${data.enrollmentToken}`,
    data,
  );
  return data;
}

async function simpleAdminAction(
  id: string,
  action: string,
  options: CommonOptions,
  input: InputSource,
  body?: unknown,
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(resolved, "POST", `/api/node-admin/admins/${encodeURIComponent(id)}/${action}`, body, input);
  emit(options.json, `admins/${id}: ${action} ok.`, { id, action });
}

export const runAdminsSuspend = (id: string, options: CommonOptions, input: InputSource = createRealInputSource()) =>
  simpleAdminAction(id, "suspend", options, input);
export const runAdminsReactivate = (id: string, options: CommonOptions, input: InputSource = createRealInputSource()) =>
  simpleAdminAction(id, "reactivate", options, input);
export const runAdminsRevoke = (id: string, options: CommonOptions, input: InputSource = createRealInputSource()) =>
  simpleAdminAction(id, "revoke", options, input);
export const runAdminsRevokeSessions = (id: string, options: CommonOptions, input: InputSource = createRealInputSource()) =>
  simpleAdminAction(id, "revoke-sessions", options, input);

export async function runAdminsActivate(
  id: string,
  fingerprint: string,
  options: CommonOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(resolved, "POST", `/api/node-admin/admins/${encodeURIComponent(id)}/activate`, { fingerprint }, input);
  emit(options.json, `admins/${id}: activated with fingerprint ${fingerprint}.`, { id, fingerprint });
}

/**
 * `PUT /admins/{id}/roles` returns the same `NodeAdminReenrollmentView` shape as `mfa reset`
 * (lane A1 M-2): changing the roles of a still-pending admin forces them back to
 * `pending_enrollment` (keys/TOTP invalidated), and only a root caller gets a fresh enrollment
 * token for it.
 */
export async function runAdminsSetRoles(
  id: string,
  roleIds: string[],
  options: CommonOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const data = await mutateWithStepUpRetry<ReenrollmentResponse>(
    resolved,
    "PUT",
    `/api/node-admin/admins/${encodeURIComponent(id)}/roles`,
    { roleIds },
    input,
  );

  if (data.enrollmentToken) {
    emit(
      options.json,
      `admins/${id}: roles set to [${roleIds.join(", ")}]. This reset the admin to ` +
        `'${data.admin.status}' — new enrollment token (shown once, expires ${data.enrollmentExpiresAt}):\n${data.enrollmentToken}`,
      data,
    );
    return;
  }
  if (data.admin.status === "pending_enrollment") {
    emit(
      options.json,
      `admins/${id}: roles set to [${roleIds.join(", ")}]. This admin was still pending, so the ` +
        `role change reset it to 'pending_enrollment' (keys/TOTP invalidated). No enrollment token ` +
        `was issued to you — only root can hand out a new one; ask them to re-run this (or 'mfa reset').`,
      data,
    );
    return;
  }
  emit(options.json, `admins/${id}: roles set to [${roleIds.join(", ")}].`, data);
}
