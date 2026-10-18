import { requireSession, resolveProfile } from "../context.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";

interface MfaResetAdmin {
  id: string;
  username: string;
  status: string;
}

/**
 * `POST /admins/{id}/mfa/reset` (confirmed against `NodeAdminMfaResetView`, lane A1 f4d5a77e):
 * the token fields are only non-null when the CALLER is root — a peer owner still gets
 * confirmation that the reset happened (keys/sessions revoked, admin back to
 * `pending_enrollment`), just not a token they could use to re-enroll someone else's identity
 * with their own key (§invariants "MFA reset").
 */
export interface MfaResetResponse {
  admin: MfaResetAdmin;
  enrollmentToken: string | null;
  enrollmentExpiresAt: string | null;
}

export async function runMfaReset(
  adminId: string,
  options: { profile?: string; json: boolean },
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const data = await mutateWithStepUpRetry<MfaResetResponse>(
    resolved,
    "POST",
    `/api/node-admin/admins/${encodeURIComponent(adminId)}/mfa/reset`,
    undefined,
    input,
  );

  if (data.enrollmentToken) {
    emit(
      options.json,
      `MFA reset for ${adminId}. New enrollment token (shown once, expires ${data.enrollmentExpiresAt}):\n${data.enrollmentToken}`,
      data,
    );
    return;
  }

  emit(
    options.json,
    `MFA reset for ${adminId}. All of this admin's keys and sessions were revoked and the account is ` +
      `now '${data.admin.status}'. No enrollment token was issued to you — only root can hand out a new ` +
      `one (so a peer owner can't re-enroll someone else's identity with their own key). Ask whoever ` +
      `holds the root key to run 'azoa-node mfa reset ${adminId} --profile <root-profile>'.`,
    data,
  );
}
