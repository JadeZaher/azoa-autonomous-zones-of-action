import { spawnSync } from "node:child_process";
import { chmodSync } from "node:fs";
import { userInfo } from "node:os";

/**
 * Locks a file down to the current user only. POSIX: chmod 0600 (see fsutil.ts for the
 * TOCTOU-safe write path that also sets this mode at create time). Windows has no octal-mode
 * mapping, so we strip inherited ACEs and grant the current user Full Control via `icacls`,
 * then parse the re-read ACL to confirm it actually stuck (plan.md §8 CLI binding: "icacls ...
 * and verify") — not just that the username string appears somewhere in the output, which would
 * also match a path containing the username (H2 fix).
 */
export function securizeFile(path: string): void {
  if (process.platform === "win32") {
    securizeFileWindows(path);
    return;
  }
  chmodSync(path, 0o600);
}

// --- pure ACL parsing (table-tested against real icacls output samples) --------------------

export interface IcaclsAce {
  /** e.g. "DOMAIN\\user", "BUILTIN\\Administrators", or a raw SID string. */
  principal: string;
  /** Parenthesized flags in order, e.g. ["I", "F"] for an inherited Full Control ACE. */
  flags: string[];
}

function isSummaryLine(line: string): boolean {
  return /^(Successfully processed|Failed processing)/i.test(line.trim());
}

/**
 * Parses `icacls <path>` output into one entry per ACE. Pure function — no process spawning —
 * so it can be table-tested against literal sample output instead of a live Windows ACL.
 *
 * Real icacls shape: the first line is `<path> <principal>:(<flags>)...`; any further ACEs for
 * the same path are continuation lines indented to align under the first ACE (not under the
 * path); a blank line and a "Successfully processed ..." summary follow.
 */
export function parseIcaclsAces(stdout: string, path: string): IcaclsAce[] {
  const lines = stdout.split(/\r?\n/);
  const aces: IcaclsAce[] = [];
  let consumedPathPrefix = false;

  for (const rawLine of lines) {
    if (rawLine.trim().length === 0) continue;
    if (isSummaryLine(rawLine)) continue;

    let line: string;
    if (!consumedPathPrefix) {
      consumedPathPrefix = true;
      if (!rawLine.startsWith(path)) {
        throw new Error(`icacls output for '${path}' did not start with that path: '${rawLine}'`);
      }
      line = rawLine.slice(path.length).trimStart();
      if (line.length === 0) continue;
    } else {
      line = rawLine.trim();
    }

    const match = line.match(/^(.+?):((?:\([^)]*\))+)$/);
    if (!match) {
      throw new Error(`Unrecognized icacls ACE line for '${path}': '${rawLine}'`);
    }
    const principal = match[1]!;
    const flags = [...match[2]!.matchAll(/\(([^)]*)\)/g)].map((m) => m[1]!);
    aces.push({ principal, flags });
  }

  return aces;
}

export interface WindowsIdentity {
  /** Acceptable display-name forms: bare username, DOMAIN\username, whoami's own output. */
  nameCandidates: string[];
  /** SID string from `whoami /user`, when resolvable. */
  sid?: string;
}

function principalMatches(ace: IcaclsAce, identity: WindowsIdentity): boolean {
  const principalLower = ace.principal.toLowerCase();
  if (identity.nameCandidates.some((c) => c.toLowerCase() === principalLower)) return true;
  if (identity.sid && ace.principal.toUpperCase() === identity.sid.toUpperCase()) return true;
  return false;
}

/**
 * The real verification (H2): exactly one ACE, granting Full Control, to the current user, with
 * no inherited (`I`) flag (an `(I)` surviving `/inheritance:r` means inheritance removal silently
 * failed). Pure function over already-fetched `icacls` output + identity — the only I/O this
 * needs is passed in, so it is unit-testable without a live Windows ACL.
 */
export function verifyAcl(stdout: string, path: string, identity: WindowsIdentity): void {
  const aces = parseIcaclsAces(stdout, path);
  if (aces.length !== 1) {
    throw new Error(
      `Expected exactly one ACL entry on '${path}', found ${aces.length}: ${JSON.stringify(aces)}`,
    );
  }
  const [ace] = aces as [IcaclsAce];
  if (ace.flags.includes("I")) {
    throw new Error(`ACL on '${path}' contains an inherited entry — /inheritance:r did not take effect: ${JSON.stringify(ace)}`);
  }
  if (!ace.flags.includes("F")) {
    throw new Error(`ACL on '${path}' does not grant Full Control: ${JSON.stringify(ace)}`);
  }
  if (!principalMatches(ace, identity)) {
    throw new Error(
      `ACL on '${path}' grants Full Control to '${ace.principal}', which does not match the current user ` +
        `(tried: ${identity.nameCandidates.join(", ")}${identity.sid ? `, SID ${identity.sid}` : ""}).`,
    );
  }
}

// --- process-touching glue (not unit-tested directly; verifyAcl/parseIcaclsAces are) --------

function currentWindowsUsername(): string {
  return process.env.USERNAME ?? userInfo().username;
}

/** Best-effort identity lookup via `whoami` — several forms, since icacls can print any of them. */
function resolveCurrentWindowsIdentity(): WindowsIdentity {
  const username = currentWindowsUsername();
  const nameCandidates = new Set<string>([username]);
  const domain = process.env.USERDOMAIN;
  if (domain) nameCandidates.add(`${domain}\\${username}`);

  const whoami = spawnSync("whoami", [], { encoding: "utf8" });
  if (whoami.status === 0 && whoami.stdout.trim()) {
    nameCandidates.add(whoami.stdout.trim());
  }

  let sid: string | undefined;
  const whoamiUser = spawnSync("whoami", ["/user", "/fo", "csv", "/nh"], { encoding: "utf8" });
  if (whoamiUser.status === 0 && whoamiUser.stdout.trim()) {
    const fields = whoamiUser.stdout.trim().split(",").map((s) => s.replace(/^"|"$/g, ""));
    if (fields.length >= 2 && fields[1]) sid = fields[1];
  }

  return { nameCandidates: [...nameCandidates], sid };
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

  const check = spawnSync("icacls", [path], { encoding: "utf8" });
  if (check.status !== 0 || !check.stdout) {
    throw new Error(`icacls verification failed for ${path}: ${check.stderr || "no output"}`);
  }
  verifyAcl(check.stdout, path, resolveCurrentWindowsIdentity());
}
