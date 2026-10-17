// Round-4 static merge check (plan §3.3). Node, no dependencies, no tests run.
// Usage: node scripts/round4/merge-check.mjs [repoRoot]   — exit 1 with one message per violation.
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { execSync } from "node:child_process";
import { join, resolve } from "node:path";

const root = resolve(process.argv[2] ?? process.cwd());
const violations = [];
const read = (relative) => readFileSync(join(root, relative), "utf8");
const stripLineComments = (source) => source.replace(/\/\/[^\n]*/g, "");
const normalize = (text) => text.replace(/\s+/g, " ").trim();

// Program.cs ordering (root AGENTS.md §Program.cs registration order).
const program = stripLineComments(read("Program.cs"));
const kycRegistrations = [...program.matchAll(/\.(?:Try)?Add(?:Scoped|Singleton|Transient)\s*<\s*[\w.]*\bIRealValueKycGate\b\s*[,>]/g)];
if (kycRegistrations.length !== 1)
  violations.push(`Program.cs: expected exactly one IRealValueKycGate registration, found ${kycRegistrations.length}`);
const ordered = [
  ["IRealValueKycGate registration", kycRegistrations[0]?.index ?? -1],
  ["AddFiatOnRamp", program.search(/\.AddFiatOnRamp\s*\(/)],
  ["AddFiatReversalPipeline", program.search(/\.AddFiatReversalPipeline\s*\(/)],
  ["AddReversalRiskValueGate", program.search(/\.AddReversalRiskValueGate\s*\(/)],
];
for (const [name, index] of ordered) if (index < 0) violations.push(`Program.cs: ${name} not found`);
for (let i = 1; i < ordered.length; i++) {
  const [previousName, previous] = ordered[i - 1];
  const [name, index] = ordered[i];
  if (previous >= 0 && index >= 0 && previous > index)
    violations.push(`Program.cs: ${previousName} must come before ${name}`);
}
if (!/\.Add\w*\s*<\s*[\w.]*\bIWormholeAdapter\b/.test(program))
  violations.push("Program.cs: IWormholeAdapter is no longer registered (WormholeProtocol needs it)");

// Merged lane records: every `hunk path=<file>` block is still present verbatim (whitespace-normalized).
const trackDir = join(root, "conductor/tracks/platform-stub-closeout_20261015");
const records = existsSync(trackDir) ? readdirSync(trackDir).filter((name) => /^round4-lane-.*\.md$/.test(name)) : [];
for (const name of records) {
  const text = readFileSync(join(trackDir, name), "utf8").replace(/\r\n/g, "\n");
  const frontmatter = text.match(/^---\n([\s\S]*?)\n---/);
  if (!frontmatter || !/^merged:\s*\S+/m.test(frontmatter[1])) continue;
  for (const block of text.matchAll(/^(`{3,})hunk path=(\S+)[^\n]*\n([\s\S]*?)\n\1\s*$/gm)) {
    const [, , path, body] = block;
    const target = join(root, path);
    if (!existsSync(target)) { violations.push(`${name}: hunk target ${path} does not exist`); continue; }
    if (!normalize(readFileSync(target, "utf8")).includes(normalize(body)))
      violations.push(`${name}: hunk for ${path} is no longer present verbatim`);
  }
}

// No tracked nuget.config may point at a local filesystem source.
const tracked = execSync("git ls-files", { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }).split(/\r?\n/);
for (const path of tracked.filter((file) => /(^|\/)nuget\.config$/i.test(file))) {
  for (const match of read(path).matchAll(/value\s*=\s*"([^"]*)"/gi)) {
    if (/^(?:[A-Za-z]:[\/]|\\|\/|\.{1,2}[\/]|file:)/.test(match[1]) || /%\w+%|\$\(/.test(match[1]))
      violations.push(`${path}: local package source "${match[1]}"`);
  }
}

if (violations.length) {
  for (const violation of violations) console.error(`merge-check: ${violation}`);
  process.exit(1);
}
console.log(`merge-check: ok (${records.length} lane records scanned)`);
