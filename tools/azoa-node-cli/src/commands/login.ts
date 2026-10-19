import { AzoaCliError } from "../errors.js";
import { signCanonical } from "../crypto/canonical.js";
import { AzoaHttpClient } from "../http/client.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { getProfile, resolveProfileName } from "../storage/config.js";
import { readKeyFile, readKeyFileMeta } from "../storage/keyFile.js";
import { saveSession, type StoredSession } from "../storage/session.js";

export interface LoginOptions {
  profile?: string;
  root?: boolean;
  totp?: string;
  json: boolean;
  /**
   * M6 (--json purity): set by `mutate.ts` when this login is an internal
   * step-up re-authentication, not the user's own `login` invocation. Status
   * goes to stderr instead of stdout so a `--json` caller's stdout still
   * contains exactly one JSON object — the actual command's result, not an
   * extra blob from the retry machinery.
   */
  quiet?: boolean;
}

interface ChallengeResponse {
  challengeId: string;
  nonce: string;
  expiresAt: string;
  audience: string;
}

interface SessionResponse {
  accessToken: string;
  expiresAt: string;
  /** Opaque session-list/revoke id — the real sid lives only inside accessToken (lane A1 M-1). */
  handle: string;
  principal: string;
  permissions: string[];
  admin?: { id: string; username: string; roles: string[] };
}

/**
 * Runs the full §3/§8 challenge → canonical-sign → session flow for one
 * profile. Returns the stored session so `commands/api.ts` can reuse it for
 * the step-up re-login-and-retry path without re-reading from disk.
 */
export async function runLogin(
  options: LoginOptions,
  input: InputSource = createRealInputSource(),
): Promise<StoredSession> {
  const name = resolveProfileName(options.profile);
  const profile = getProfile(name);
  if (options.root && profile.kind !== "root") {
    throw AzoaCliError.usage(`Profile '${name}' is kind '${profile.kind}', not root.`);
  }

  const meta = readKeyFileMeta(profile.keyName);
  const client = new AzoaHttpClient(profile.node);
  const purpose = profile.kind === "root" ? "root_login" : "admin_login";

  const { data: challenge } = await client.call<ChallengeResponse>("POST", "/api/node-admin/auth/challenge", {
    purpose,
    fingerprint: meta.fingerprint,
  });

  // H1: audience must match the profile's pinned node origin EXACTLY, or abort before signing anything.
  if (challenge.audience !== profile.node) {
    throw new AzoaCliError(
      `Audience mismatch: challenge audience '${challenge.audience}' does not match profile node ` +
        `'${profile.node}'. Refusing to sign — this could be a relay/MITM attempt.`,
      3,
      "AUTH_FAILED",
    );
  }

  let passphrase: string | null = null;
  if (meta.encrypted) {
    passphrase = await input.readSecret("Key passphrase: ");
  }
  const keyPair = await readKeyFile(profile.keyName, passphrase);

  // H2: sign the server-returned strings byte-for-byte; never re-serialize them.
  const signature = signCanonical(keyPair.privateKey, [
    purpose,
    challenge.audience,
    challenge.challengeId,
    challenge.nonce,
    challenge.expiresAt,
  ]);

  let totp: string | undefined;
  if (profile.kind === "admin") {
    totp = options.totp ?? (await input.readVisible("TOTP code: "));
  }

  const { data: sessionResponse } = await client.call<SessionResponse>("POST", "/api/node-admin/auth/session", {
    challengeId: challenge.challengeId,
    publicKey: meta.publicKey,
    signature,
    ...(totp ? { totp } : {}),
  });

  const session: StoredSession = {
    accessToken: sessionResponse.accessToken,
    expiresAt: sessionResponse.expiresAt,
    handle: sessionResponse.handle,
    principal: sessionResponse.principal,
    permissions: sessionResponse.permissions,
    authTime: new Date().toISOString(),
    admin: sessionResponse.admin,
  };
  saveSession(name, session);

  if (options.quiet) {
    process.stderr.write(`[azoa-node] Re-authenticated as ${session.principal} on profile '${name}' (step-up).\n`);
  } else {
    emit(
      options.json,
      `Logged in as ${session.principal} on profile '${name}' (${session.permissions.length} permissions).`,
      { profile: name, principal: session.principal, permissions: session.permissions, expiresAt: session.expiresAt },
    );
  }

  return session;
}
