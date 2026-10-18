import { requireSession, resolveProfile } from "../context.js";
import { AzoaCliError } from "../errors.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";

export interface RootRevokeSessionsOptions {
  profile?: string;
  yes: boolean;
  json: boolean;
}

/**
 * `POST /root/revoke-sessions` (lane A1 L-2, tightened NEW-2): ends every root session by
 * bumping `root_session_revision` — **root-only**; even an owner-capable admin gets
 * `PERMISSION_DENIED` server-side. The CLI fails fast locally instead of making a doomed round
 * trip, since the active profile's kind is already known.
 */
export async function runRootRevokeSessions(
  options: RootRevokeSessionsOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);

  // Checked before requireSession: this is a wrong-profile-kind problem, not a missing-session
  // one, and we already know the answer from local config without a round trip.
  if (resolved.profile.kind !== "root") {
    throw new AzoaCliError(
      "Only a root session can end root sessions. To cut off root instead, rotate " +
        "NodeAdmin__RootKeyRevision / root keys via 'azoa-node root env --railway --remove <fp>'.",
      4,
      "PERMISSION_DENIED",
    );
  }
  requireSession(resolved);

  if (!options.yes) {
    const answer = await input.readVisible(
      "This ends every root session on this node, including this one. Type 'yes' to confirm: ",
    );
    if (answer.trim().toLowerCase() !== "yes") {
      throw AzoaCliError.usage("Aborted: confirmation not given (pass --yes to skip the prompt).");
    }
  }

  await mutateWithStepUpRetry<boolean>(resolved, "POST", "/api/node-admin/root/revoke-sessions", undefined, input);
  emit(options.json, "Every root session on this node has been revoked.", { ok: true });
}
