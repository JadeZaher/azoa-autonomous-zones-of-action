import { generateEd25519KeyPair } from "../crypto/keys.js";
import { checkKeyFileLocation, writeKeyFile } from "../storage/keyFile.js";
import { AzoaCliError } from "../errors.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { promptNewPassphrase } from "../io/passphrase.js";
import type { ProfileKind } from "../storage/config.js";
import { assertValidName } from "../validation.js";
import { guardKeyOverwrite } from "./keyGuards.js";

export interface KeygenOptions {
  name: string;
  kind: ProfileKind;
  passphrase: boolean;
  force: boolean;
  json: boolean;
}

export async function runKeygen(
  options: KeygenOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  assertValidName(options.name, "--name");
  if (options.kind === "root" && !options.passphrase) {
    throw AzoaCliError.usage("A passphrase is mandatory for root keys (plan.md §8 CLI binding).");
  }

  await guardKeyOverwrite(options.name, options.kind, options.force, input);

  const warning = checkKeyFileLocation(options.name);
  if (warning.oneDrive) {
    process.stderr.write(
      `Warning: ~/.azoa/keys/${options.name}.key resolves under a OneDrive-synced path. ` +
        "Private key material will be uploaded to the cloud.\n",
    );
  }

  const passphrase = options.passphrase ? await promptNewPassphrase(input) : null;

  const keyPair = generateEd25519KeyPair();
  await writeKeyFile(options.name, keyPair, options.kind, passphrase);

  emit(
    options.json,
    `Generated ${options.kind} key '${options.name}'. Public key: ${keyPair.publicKeyB64Url} Fingerprint: ${keyPair.fingerprint}`,
    { name: options.name, kind: options.kind, publicKey: keyPair.publicKeyB64Url, fingerprint: keyPair.fingerprint },
  );
}
