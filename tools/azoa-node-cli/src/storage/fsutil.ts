import { mkdirSync, readFileSync, writeFileSync, existsSync, unlinkSync, renameSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { dirname, join } from "node:path";
import { securizeFile } from "./permissions.js";

export function readJsonFile<T>(path: string): T | null {
  if (!existsSync(path)) return null;
  return JSON.parse(readFileSync(path, "utf8")) as T;
}

/**
 * M1 (POSIX TOCTOU): writes to a sibling temp file with `{ mode: 0o600, flag: "wx" }` — created
 * exclusively, already at the final mode, so there is never a window where the data exists
 * world-/group-readable (as a plain `writeFileSync` + later `chmod` would have). Then renames
 * atomically over the real path. `securizeFile` still runs after the rename: on POSIX it's a
 * harmless re-assertion of the mode the temp file already had (rename preserves it); on Windows
 * it's the only place permissions are set at all (no POSIX mode bits), so it has to run on the
 * final path.
 */
export function writeJsonFileSecure(path: string, value: unknown): void {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  const tmpPath = join(dirname(path), `.${randomBytes(8).toString("hex")}.tmp`);
  writeFileSync(tmpPath, `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600, flag: "wx" });
  renameSync(tmpPath, path);
  securizeFile(path);
}

export function deleteFileIfExists(path: string): void {
  if (existsSync(path)) {
    unlinkSync(path);
  }
}
