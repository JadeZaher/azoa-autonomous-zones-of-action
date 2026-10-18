import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";

export interface MfaResetResponse {
  enrollmentToken: string;
  enrollmentExpiresAt: string;
}

export async function runMfaReset(adminId: string, options: { profile?: string; json: boolean }): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<MfaResetResponse>(
    "POST",
    `/api/node-admin/admins/${encodeURIComponent(adminId)}/mfa/reset`,
  );
  emit(
    options.json,
    `MFA reset for ${adminId}. New enrollment token (shown once, expires ${data.enrollmentExpiresAt}):\n${data.enrollmentToken}`,
    data,
  );
}
