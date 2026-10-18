import { addKeyMessage } from "../crypto/canonical.js";
import { generateEd25519KeyPair, signMessage } from "../crypto/keys.js";
import { AzoaCliError } from "../errors.js";
import { requireSession, resolveProfile } from "../context.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { promptNewPassphrase } from "../io/passphrase.js";
import { mutateWithStepUpRetry } from "../mutate.js";
import { writeKeyFile } from "../storage/keyFile.js";
import { assertValidName } from "../validation.js";
import { deleteKeyFileQuietly, guardKeyOverwrite } from "./keyGuards.js";

export interface KeysAddOptions {
  profile?: string;
  targetAdminId?: string;
  keyName: string;
  label: string;
  passphrase: boolean;
  force: boolean;
  json: boolean;
}

/** H2: `POST .../keys` sits behind a `*Recent` (step-up) policy — `mutateWithStepUpRetry` handles it. */
export async function runKeysAdd(
  options: KeysAddOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  assertValidName(options.keyName, "--key-name");
  const resolved = resolveProfile(options.profile);
  const session = requireSession(resolved);

  const adminId = options.targetAdminId ?? session.admin?.id;
  if (!adminId) {
    throw AzoaCliError.usage("No target admin id: pass one explicitly, or log in as a named admin first.");
  }

  await guardKeyOverwrite(options.keyName, resolved.profile.kind, options.force, input);
  const passphrase = options.passphrase ? await promptNewPassphrase(input) : null;

  const keyPair = generateEd25519KeyPair();
  // H2: the NEW key proves possession of itself — never "signed by an existing key".
  const message = addKeyMessage(resolved.profile.node, adminId, keyPair.fingerprint);
  const signature = signMessage(keyPair.privateKey, message);

  // M4: the key file must exist on disk before the server call that depends on it, so a crash
  // between "server accepted the key" and "we saved it" can never lose the only copy of a
  // private key the server now expects to see again. If the server call fails, the file is
  // useless (the key was never registered) — delete it rather than leave orphaned clutter.
  await writeKeyFile(options.keyName, keyPair, resolved.profile.kind, passphrase);
  try {
    await mutateWithStepUpRetry(
      resolved,
      "POST",
      `/api/node-admin/admins/${encodeURIComponent(adminId)}/keys`,
      { publicKey: keyPair.publicKeyB64Url, label: options.label, signature },
      input,
    );
  } catch (err) {
    deleteKeyFileQuietly(options.keyName);
    throw err;
  }

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
