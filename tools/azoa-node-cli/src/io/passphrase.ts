import { AzoaCliError } from "../errors.js";
import type { InputSource } from "./input.js";

/** M3: one shared rule everywhere a new key's passphrase is collected. */
export const MIN_PASSPHRASE_LENGTH = 12;

/**
 * Collects (confirm, minimum length, never empty) a passphrase for a new key. Prefers
 * `AZOA_KEY_PASSPHRASE` when set (documented escape hatch for non-interactive use — never pass a
 * passphrase as a CLI argument, where it would appear in shell history and process listings).
 *
 * Builds on `io/input.ts`'s FIFO stdin line reader rather than any separate "is stdin already
 * used up" tracking: that reader already queues piped lines in strict order across an entire
 * invocation (join-report.md fix), so `enroll`'s token → this passphrase prompt → its
 * confirmation → the TOTP prompt all correctly read their own line as long as a script piped
 * one. If a script pipes too few lines, the reader resolves the extra reads with `""` at EOF
 * instead of hanging — which the empty-passphrase check below already turns into a clear error
 * that also points at the env-var escape hatch, so there is nothing extra to special-case here.
 */
export async function promptNewPassphrase(input: InputSource): Promise<string> {
  const fromEnv = process.env.AZOA_KEY_PASSPHRASE;
  if (fromEnv !== undefined) {
    if (fromEnv.length === 0) {
      throw AzoaCliError.usage("AZOA_KEY_PASSPHRASE is set but empty.");
    }
    if (fromEnv.length < MIN_PASSPHRASE_LENGTH) {
      throw AzoaCliError.usage(`AZOA_KEY_PASSPHRASE must be at least ${MIN_PASSPHRASE_LENGTH} characters.`);
    }
    return fromEnv;
  }
  const first = await input.readSecret("Passphrase: ");
  if (first.length === 0) {
    throw AzoaCliError.usage(
      "Passphrase cannot be empty (if stdin ran out of piped lines, set AZOA_KEY_PASSPHRASE instead of prompting).",
    );
  }
  const second = await input.readSecret("Confirm passphrase: ");
  if (first !== second) {
    throw AzoaCliError.usage("Passphrases did not match.");
  }
  if (first.length < MIN_PASSPHRASE_LENGTH) {
    throw AzoaCliError.usage(`Passphrase must be at least ${MIN_PASSPHRASE_LENGTH} characters.`);
  }
  return first;
}
