// Scans a git diff for any .env value (length >= 8). Prints key names only, never values; exit 1 on a hit.
// Usage: node scripts/round4/secret-scan.mjs [repo] [diff-args]   (default diff-args: --cached)
// .env files are read from the repo and from the main worktree, so a lane worktree (no .env) is still covered.
import { readFileSync, existsSync } from "node:fs";
import { execSync } from "node:child_process";
import { dirname, resolve } from "node:path";

const repo = resolve(process.argv[2] ?? process.cwd());
const diffArgs = process.argv[3] ?? "--cached";
const commonDir = execSync("git rev-parse --path-format=absolute --git-common-dir", { cwd: repo, encoding: "utf8" }).trim();
const roots = [...new Set([repo, dirname(commonDir)])];
const envFiles = roots
  .flatMap((root) => [".env", ".env.local", "frontend/.env.local", "frontend/.env"].map((f) => `${root}/${f}`))
  .filter(existsSync);
const values = [];
for (const file of envFiles) {
  for (const line of readFileSync(file, "utf8").split(/\r?\n/)) {
    const match = line.match(/^\s*([A-Za-z0-9_]+)\s*=\s*(.*)\s*$/);
    if (!match) continue;
    const value = match[2].replace(/^["']|["']$/g, "");
    if (value.length >= 8) values.push({ key: match[1], value });
  }
}
const diff = execSync(`git diff ${diffArgs}`, { cwd: repo, encoding: "utf8", maxBuffer: 512 * 1024 * 1024 });
const hits = [...new Set(values.filter(({ value }) => diff.includes(value)).map(({ key }) => key))];
console.log(`env files: ${envFiles.length}, values checked: ${values.length}, hits: ${hits.length ? hits.join(",") : "none"}`);
process.exit(hits.length ? 1 : 0);
