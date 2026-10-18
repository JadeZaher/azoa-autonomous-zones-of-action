import { addKeyMessage } from "../crypto/canonical.js";
import { generateEd25519KeyPair, signMessage } from "../crypto/keys.js";
import { AzoaCliError } from "../errors.js";
import { requireSession, resolveProfile } from "../context.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";
import { writeKeyFile } from "../storage/keyFile.js";

export interface KeysAddOptions {
  profile?: string;
  targetAdminId?: string;
  keyName: string;
  label: string;
  passphrase: boolean;
  json: boolean;
}

/** H2: `POST .../keys` sits behind a `*Recent` (step-up) policy — `mutateWithStepUpRetry` handles it. */
export async function runKeysAdd(
  options: KeysAddOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  const session = requireSession(resolved);

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

  await mutateWithStepUpRetry(
    resolved,
    "POST",
    `/api/node-admin/admins/${encodeURIComponent(adminId)}/keys`,
    { publicKey: keyPair.publicKeyB64Url, label: options.label, signature },
    input,
  );

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
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(
    resolved,
    "DELETE",
    `/api/node-admin/admins/${encodeURIComponent(adminId)}/keys/${encodeURIComponent(fingerprint)}`,
    undefined,
    input,
  );
  emit(options.json, `Revoked key ${fingerprint} from admin ${adminId}.`, { adminId, fingerprint });
}
