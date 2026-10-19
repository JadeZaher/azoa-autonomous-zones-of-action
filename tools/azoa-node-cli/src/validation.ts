import { AzoaCliError } from "./errors.js";

/** Every local name that becomes a filesystem path component (key names, profile names). */
export const NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

export function assertValidName(name: string, label: string): void {
  if (!NAME_PATTERN.test(name)) {
    throw AzoaCliError.usage(`Invalid ${label} '${name}': must match ${NAME_PATTERN} (letters, digits, '_', '-', 1-64 chars).`);
  }
}
