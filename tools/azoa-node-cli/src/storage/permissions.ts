import { spawnSync } from "node:child_process";
import { chmodSync } from "node:fs";
import { userInfo } from "node:os";

/**
 * Locks a file down to the current user only. POSIX: chmod 0600. Windows has
 * no octal-mode mapping, so we strip inherited ACEs and grant the current
 * user Full Control via `icacls`, then re-read the ACL to confirm it stuck
 * (plan.md §8 CLI binding: "icacls ... and verify").
 */
export function securizeFile(path: string): void {
  if (process.platform === "win32") {
    securizeFileWindows(path);
    return;
  }
  chmodSync(path, 0o600);
}

function currentWindowsUsername(): string {
  return process.env.USERNAME ?? userInfo().username;
}

function securizeFileWindows(path: string): void {
  const username = currentWindowsUsername();
  const grant = spawnSync(
    "icacls",
    [path, "/inheritance:r", "/grant:r", `${username}:F`],
    { encoding: "utf8" },
  );
  if (grant.status !== 0) {
    throw new Error(
      `icacls failed to secure ${path} (exit ${grant.status}): ${grant.stderr || grant.stdout}`,
    );
  }
  verifyWindowsAcl(path, username);
}

function verifyWindowsAcl(path: string, username: string): void {
  const check = spawnSync("icacls", [path], { encoding: "utf8" });
  if (check.status !== 0 || !check.stdout) {
    throw new Error(`icacls verification failed for ${path}: ${check.stderr || "no output"}`);
  }
  const output = check.stdout.toLowerCase();
  if (!output.includes(username.toLowerCase())) {
    throw new Error(`icacls verification failed: ${username} not found in ACL for ${path}`);
  }
}
