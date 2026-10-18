import { generateEd25519KeyPair } from "../crypto/keys.js";
import { checkKeyFileLocation, writeKeyFile } from "../storage/keyFile.js";
import { AzoaCliError } from "../errors.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import type { ProfileKind } from "../storage/config.js";

export interface KeygenOptions {
  name: string;
  kind: ProfileKind;
  passphrase: boolean;
  json: boolean;
}

export async function runKeygen(
  options: KeygenOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  if (options.kind === "root" && !options.passphrase) {
    throw AzoaCliError.usage("A passphrase is mandatory for root keys (plan.md §8 CLI binding).");
  }

  const warning = checkKeyFileLocation(options.name);
  if (warning.oneDrive) {
    process.stderr.write(
      `Warning: ~/.azoa/keys/${options.name}.key resolves under a OneDrive-synced path. ` +
        "Private key material will be uploaded to the cloud.\n",
    );
  }

  let passphrase: string | null = null;
  if (options.passphrase) {
    const first = await input.readSecret("Passphrase: ");
    const second = await input.readSecret("Confirm passphrase: ");
    if (first !== second) {
      throw AzoaCliError.usage("Passphrases did not match.");
    }
    if (first.length < 8) {
      throw AzoaCliError.usage("Passphrase must be at least 8 characters.");
    }
    passphrase = first;
  }

  const keyPair = generateEd25519KeyPair();
  await writeKeyFile(options.name, keyPair, options.kind, passphrase);

  emit(
    options.json,
    `Generated ${options.kind} key '${options.name}'. Public key: ${keyPair.publicKeyB64Url} Fingerprint: ${keyPair.fingerprint}`,
    { name: options.name, kind: options.kind, publicKey: keyPair.publicKeyB64Url, fingerprint: keyPair.fingerprint },
  );
}
