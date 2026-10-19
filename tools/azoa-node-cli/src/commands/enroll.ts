import qrcodeTerminal from "qrcode-terminal";
import { enrollMessage } from "../crypto/canonical.js";
import { generateEd25519KeyPair, signMessage } from "../crypto/keys.js";
import { AzoaCliError } from "../errors.js";
import { AzoaHttpClient } from "../http/client.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { promptNewPassphrase } from "../io/passphrase.js";
import { upsertProfile } from "../storage/config.js";
import { writeKeyFile } from "../storage/keyFile.js";
import { assertValidName } from "../validation.js";
import { guardKeyOverwrite, warnRetainedKey } from "./keyGuards.js";

export interface EnrollOptions {
  node: string;
  profile: string;
  keyName: string;
  label: string;
  passphrase: boolean;
  force: boolean;
  json: boolean;
}

interface EnrollStartResponse {
  enrollmentId: string;
  otpauthUri: string;
  totpSecretBase32: string;
  /** Server's real configured audience — not an echo of anything the client sent (there is no
   * profile/challenge yet at this point), so this is a genuine H1-style check, not a formality. */
  audience: string;
  /** The fingerprint the server computed from the public key we just submitted. */
  fingerprint: string;
}

interface EnrollCompleteResponse {
  adminId: string;
  status: string;
  fingerprint: string;
}

/**
 * §8 H3: pending_enrollment -> /enroll/start (binds exactly one key) ->
 * /enroll/complete (TOTP + signature) -> pending_activation. The admin stays
 * inactive until a dominating admin or root runs `admins activate --fingerprint`
 * against the printed fingerprint — this command never claims "active".
 */
export async function runEnroll(
  options: EnrollOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  if (!/^https?:\/\//i.test(options.node)) {
    throw AzoaCliError.usage(`--origin must be a full origin (https://host[:port]), got '${options.node}'.`);
  }
  assertValidName(options.profile, "--profile-name");
  assertValidName(options.keyName, "--key-name");
  await guardKeyOverwrite(options.keyName, "admin", options.force, input);

  // H3: CLI reads the token from a prompt/stdin, never argv. `readPipedStdin`/`readSecret` share
  // one FIFO line queue over the real stdin stream (src/io/input.ts), so this token read and the
  // passphrase prompt right below correctly get separate, in-order lines from the same piped
  // script instead of the first read draining the whole stream.
  const token = (await input.readPipedStdin()) ?? (await input.readSecret("Enrollment token: "));
  if (!token) {
    throw AzoaCliError.usage("No enrollment token provided.");
  }

  const passphrase = options.passphrase ? await promptNewPassphrase(input) : null;

  const keyPair = generateEd25519KeyPair();
  const client = new AzoaHttpClient(options.node);
  await writeKeyFile(options.keyName, keyPair, "admin", passphrase);

  let start: EnrollStartResponse;
  try {
    const result = await client.call<EnrollStartResponse>("POST", "/api/node-admin/enroll/start", {
      enrollmentToken: token,
      publicKey: keyPair.publicKeyB64Url,
      label: options.label,
    });
    start = result.data;
  } catch (error) {
    warnRetainedKey(options.keyName);
    throw error;
  }

  // Lane A1 contract deviation: /enroll/start echoes back the server's real NodeAdmin__Audience
  // and the fingerprint it derived from our public key. Treat both like the H1 audience check on
  // login — abort before any further step (QR, prompt, /enroll/complete) if either disagrees.
  if (start.audience !== options.node) {
    throw new AzoaCliError(
      `Audience mismatch: /enroll/start reported audience '${start.audience}' but --origin was ` +
        `'${options.node}'. Refusing to continue enrollment — this could be a relay/MITM attempt.`,
      3,
      "AUTH_FAILED",
    );
  }
  if (start.fingerprint !== keyPair.fingerprint) {
    throw new AzoaCliError(
      `Fingerprint mismatch: /enroll/start reported fingerprint '${start.fingerprint}' for the key ` +
        `we just submitted, but it does not match our local key's fingerprint '${keyPair.fingerprint}'. ` +
        `Refusing to continue enrollment.`,
      3,
      "AUTH_FAILED",
    );
  }

  if (!options.json) {
    qrcodeTerminal.generate(start.otpauthUri, { small: true });
  }
  emit(
    options.json,
    `Scan the QR code above with your authenticator app, or enter this secret manually: ${start.totpSecretBase32}`,
    { otpauthUri: start.otpauthUri, totpSecretBase32: start.totpSecretBase32 },
  );

  const totp = await input.readVisible("Authenticator code: ");
  const signature = signMessage(keyPair.privateKey, enrollMessage(options.node, start.enrollmentId));

  let complete: EnrollCompleteResponse;
  try {
    const result = await client.call<EnrollCompleteResponse>("POST", "/api/node-admin/enroll/complete", {
      enrollmentId: start.enrollmentId,
      totp,
      signature,
    });
    complete = result.data;
  } catch (err) {
    warnRetainedKey(options.keyName);
    throw err;
  }

  upsertProfile(options.profile, { node: options.node, keyName: options.keyName, kind: "admin" }, true);

  emit(
    options.json,
    `Enrollment complete. Your account (${complete.adminId}) is now '${complete.status}'.\n` +
      `Tell this fingerprint to the admin who created your account so they can run ` +
      `'azoa-node admins activate ${complete.adminId} --fingerprint ${complete.fingerprint}':\n` +
      `${complete.fingerprint}`,
    { adminId: complete.adminId, status: complete.status, fingerprint: complete.fingerprint, profile: options.profile },
  );
}
