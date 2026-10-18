import qrcodeTerminal from "qrcode-terminal";
import { enrollMessage } from "../crypto/canonical.js";
import { generateEd25519KeyPair, signMessage } from "../crypto/keys.js";
import { AzoaCliError } from "../errors.js";
import { AzoaHttpClient } from "../http/client.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { upsertProfile } from "../storage/config.js";
import { keyPath, azoaHome } from "../storage/paths.js";
import { writeKeyFile } from "../storage/keyFile.js";
import { existsSync } from "node:fs";

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
    throw AzoaCliError.usage(`--node must be a full origin (https://host[:port]), got '${options.node}'.`);
  }
  if (existsSync(keyPath(options.keyName)) && !options.force) {
    throw AzoaCliError.usage(`Key '${options.keyName}' already exists under ${azoaHome()}/keys. Pass --force to overwrite or choose a different --key-name.`);
  }

  // H3: CLI reads the token from a prompt/stdin, never argv.
  const token = (await input.readPipedStdin()) ?? (await input.readSecret("Enrollment token: "));
  if (!token) {
    throw AzoaCliError.usage("No enrollment token provided.");
  }

  let passphrase: string | null = null;
  if (options.passphrase) {
    const first = await input.readSecret("Passphrase for new key: ");
    const second = await input.readSecret("Confirm passphrase: ");
    if (first !== second) throw AzoaCliError.usage("Passphrases did not match.");
    passphrase = first;
  }

  const keyPair = generateEd25519KeyPair();
  const client = new AzoaHttpClient(options.node);

  const { data: start } = await client.call<EnrollStartResponse>("POST", "/api/node-admin/enroll/start", {
    enrollmentToken: token,
    publicKey: keyPair.publicKeyB64Url,
    label: options.label,
  });

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

  await client.call("POST", "/api/node-admin/enroll/complete", {
    enrollmentId: start.enrollmentId,
    totp,
    signature,
  });

  await writeKeyFile(options.keyName, keyPair, "admin", passphrase);
  upsertProfile(options.profile, { node: options.node, keyName: options.keyName, kind: "admin" }, true);

  emit(
    options.json,
    `Enrollment complete. Your account is now 'pending_activation'.\n` +
      `Tell this fingerprint to the admin who created your account so they can run ` +
      `'azoa-node admins activate <your-admin-id> --fingerprint ${keyPair.fingerprint}':\n` +
      `${keyPair.fingerprint}`,
    { fingerprint: keyPair.fingerprint, profile: options.profile },
  );
}
