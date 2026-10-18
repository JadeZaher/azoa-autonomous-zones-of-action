import { existsSync } from "node:fs";
import { AzoaCliError } from "../errors.js";
import type { InputSource } from "../io/input.js";
import type { ProfileKind } from "../storage/config.js";
import { deleteFileIfExists } from "../storage/fsutil.js";
import { azoaHome, keyPath } from "../storage/paths.js";

/**
 * H3: refuses to clobber an existing key file unless `--force`; a `--force` overwrite of a ROOT
 * key additionally requires typing the key name back to confirm (a root key is the one credential
 * that can never be silently regenerated out from under its holder).
 */
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
  if (kind === "root") {
    const typed = await input.readVisible(`This overwrites an existing ROOT key. Type '${name}' to confirm: `);
    if (typed.trim() !== name) {
      throw AzoaCliError.usage("Confirmation did not match the key name; aborting overwrite.");
    }
  }
}

/** M4: cleans up a just-written key file after the server call it was written for fails. */
export function deleteKeyFileQuietly(name: string): void {
  deleteFileIfExists(keyPath(name));
}
