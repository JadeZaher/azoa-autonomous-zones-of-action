import { addKeyMessage } from "../crypto/canonical.js";
import { generateEd25519KeyPair, signMessage } from "../crypto/keys.js";
import { AzoaCliError } from "../errors.js";
import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { writeKeyFile } from "../storage/keyFile.js";
import { isRecentAuth } from "../storage/session.js";
import { runLogin } from "./login.js";

export interface KeysAddOptions {
  profile?: string;
  targetAdminId?: string;
  keyName: string;
  label: string;
  passphrase: boolean;
  json: boolean;
}

/** H2: keys add requires a step-up (≤10 min) session; the CLI re-logs-in transparently if stale. */
export async function runKeysAdd(
  options: KeysAddOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  let resolved = resolveProfile(options.profile);
  let session = requireSession(resolved);

  if (!isRecentAuth(session.authTime)) {
    session = await runLogin({ profile: resolved.name, json: options.json }, input);
    resolved = resolveProfile(options.profile);
  }

  const adminId = options.targetAdminId ?? session.admin?.id;
  if (!adminId) {
    throw AzoaCliError.usage("No target admin id: pass one explicitly, or log in as a named admin first.");
  }

  let passphrase: string | null = null;
  if (options.passphrase) {
    passphrase = await input.readSecret("Passphrase for new key: ");
  }
  const keyPair = generateEd25519KeyPair();
  // H2: the NEW key proves possession of itself — never "signed by an existing key".
  const message = addKeyMessage(resolved.profile.node, adminId, keyPair.fingerprint);
  const signature = signMessage(keyPair.privateKey, message);

  const client = httpClientFor({ ...resolved, session });
  await client.call("POST", `/api/node-admin/admins/${encodeURIComponent(adminId)}/keys`, {
    publicKey: keyPair.publicKeyB64Url,
    label: options.label,
    signature,
  });

  await writeKeyFile(options.keyName, keyPair, resolved.profile.kind, passphrase);

  emit(
    options.json,
    `Added key '${options.keyName}' (fingerprint ${keyPair.fingerprint}) to admin ${adminId}.`,
    { adminId, keyName: options.keyName, fingerprint: keyPair.fingerprint },
  );
}

export async function runKeysRevoke(
  adminId: string,
  fingerprint: string,
  options: { profile?: string; json: boolean },
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call(
    "DELETE",
    `/api/node-admin/admins/${encodeURIComponent(adminId)}/keys/${encodeURIComponent(fingerprint)}`,
  );
  emit(options.json, `Revoked key ${fingerprint} from admin ${adminId}.`, { adminId, fingerprint });
}
