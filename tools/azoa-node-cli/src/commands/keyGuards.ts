import { existsSync } from "node:fs";
import { AzoaCliError } from "../errors.js";
import type { InputSource } from "../io/input.js";
import type { ProfileKind } from "../storage/config.js";
import { readKeyFileMeta } from "../storage/keyFile.js";
import { azoaHome, keyPath } from "../storage/paths.js";

/** Requires force for overwrites and typed confirmation for existing or replacement root keys. */
export async function guardKeyOverwrite(
  name: string,
  kind: ProfileKind,
  force: boolean,
  input: InputSource,
): Promise<void> {
  if (!existsSync(keyPath(name))) return;
  if (!force) {
    throw AzoaCliError.usage(
      `Key '${name}' already exists under ${azoaHome()}/keys. Pass --force to overwrite, or choose a different name.`,
    );
  }
  if (kind === "root" || readKeyFileMeta(name).kind === "root") {
    const typed = await input.readVisible(`This overwrites an existing ROOT key. Type '${name}' to confirm: `);
    if (typed.trim() !== name) {
      throw AzoaCliError.usage("Confirmation did not match the key name; aborting overwrite.");
    }
  }
}

/** Reports recovery guidance for a key retained after an unconfirmed registration. */
export function warnRetainedKey(name: string): void {
  console.error(`Key '${name}' was retained. Confirm its fingerprint with the node administrator before retrying; use a new key name for a fresh enrollment.`);
}
