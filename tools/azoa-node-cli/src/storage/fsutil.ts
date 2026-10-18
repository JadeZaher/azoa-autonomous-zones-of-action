import { mkdirSync, readFileSync, writeFileSync, existsSync, unlinkSync } from "node:fs";
import { dirname } from "node:path";
import { securizeFile } from "./permissions.js";

export function readJsonFile<T>(path: string): T | null {
  if (!existsSync(path)) return null;
  return JSON.parse(readFileSync(path, "utf8")) as T;
}

/** Writes JSON then locks the file to the current user (see permissions.ts). */
export function writeJsonFileSecure(path: string, value: unknown): void {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  securizeFile(path);
}

export function deleteFileIfExists(path: string): void {
  if (existsSync(path)) {
    unlinkSync(path);
  }
}
