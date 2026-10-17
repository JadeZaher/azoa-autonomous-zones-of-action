#!/usr/bin/env python3
"""Retired-vocabulary residual sweep: scripted find/replace that logs every changed line.

    python sweep.py plan [name]    # dry run -> manifest.plan.jsonl (scratch); touches nothing else
    python sweep.py apply <name>   # rewrite + git mv -> <name>; refuses to overwrite an existing manifest
    python sweep.py verify         # re-scan; prints remaining rule hits (0 = idempotent)

`apply manifest-pass3.jsonl` ids its records P3-R0001…, so earlier passes' ids (and the
review verdicts that cite them) stay stable. Rule table and protocol: README.md beside this file.
"""
from __future__ import annotations

import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
COMMAND = sys.argv[1] if len(sys.argv) > 1 else "plan"
MANIFEST = HERE / (sys.argv[2] if len(sys.argv) > 2 else "manifest.plan.jsonl")
PASS = re.fullmatch(r"manifest-pass(\d+)\.jsonl", MANIFEST.name)
ID_PREFIX = f"P{PASS.group(1)}-" if PASS else ""
RULES_SHA256 = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()[:16]

TEXT_EXTENSIONS = {
    ".cs", ".ts", ".tsx", ".js", ".mjs", ".json", ".jsonl", ".surql", ".md",
    ".yml", ".yaml", ".ps1", ".sh", ".txt", ".csproj", ".py",
}

# Never rewritten: each documents the rename itself, is generated, or is mirrored byte-for-byte elsewhere.
EXCLUDED_PREFIXES = (
    "conductor/tracks/terminology-neutralization-cutover_20260905/",
    "Persistence/SurrealDb/Generated/",
    "Persistence/SurrealDb/CompatibilityBaselines/",
    ".omc/",
)
EXCLUDED_FILES = {
    "conductor/RUNBOOK.md",
    "scripts/d28_rename.py",
    "tests/AZOA.WebAPI.Tests/Core/SurrealDeploymentLeastPrivilegeContractTests.cs",
    "tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs",
    "conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md",
    "tests/AZOA.WebAPI.Tests/Architecture/RetiredVocabularyGuardTests.cs",
    "tests/AZOA.WebAPI.Tests/Architecture/AGENTS.md",
}
EXCLUDED_PARTS = {"node_modules", "bin", "obj", ".next", "dist"}

# Quest/Avatar rules skip these: consumer (ArdaNova) vocabulary, or the third-party Avatar primitive.
NOUN_RULES_SKIP_PREFIXES = (
    "conductor/product.md",
    "conductor/tracks/ardanova-production-readiness-waves_20260907/",
    "frontend/src/components/ui/avatar.tsx",
    "frontend/src/components/layout/header.tsx",
)

# A line narrating a rename ("Avatar→User", "star_odk -> ...") keeps its old nouns.
PROTECTED_LINE = re.compile(
    r"(?i)(avatar|quest|star[\w-]*|odk)`?\s{0,2}(?:→|\s->)\s{0,2}`?"
    r"(?:user|action|bundle|actiongraphbundle|action[ _-]graph[ _-]bundle)")

# Review reverts: verbatim history that must keep the old name (a released tag, a quoted finding).
HISTORICAL_LINES = {
    ("CHANGELOG.md", "released as STAR-ODK"),
    ("conductor/tracks/ardanova-production-readiness-waves_20260907/REVIEW-EVIDENCE.json", "pre-value-rail product"),
    # The stub-closeout ledger quotes the guard's own word list.
    ("conductor/tracks/platform-stub-closeout_20261015/round3-join-ledger.md", "quest/avatar/STAR/ODK/game-word renames"),
    ("conductor/tracks/platform-stub-closeout_20261015/round3-join-ledger.md", "fails on any `quest`/`avatar`"),
}

SENTENCE_START = set("\"'.:|(#>*-`[")


def _odk_prose(match: re.Match) -> str:
    prefix = match.string[: match.start()].rstrip()
    word = "Bundle" if not prefix or prefix[-1] in SENTENCE_START else "bundle"
    return word + match.group(1)


STAR_RULES = [
    ("S1-track-id", re.compile(r"star-odk-ecosystem-tree"), "action-graph-bundle-ecosystem-tree"),
    ("S2-track-id", re.compile(r"(?<![\w-])star-api(?![\w-])"), "action-graph-bundle-api"),
    ("S3-prose", re.compile(r"\b(?:STAR|Star)[ -](?:ODK|Odk)(s?)\b"), r"Action Graph Bundle\1"),
    ("S4-slug", re.compile(r"\bstar([-_])odk"), lambda m: "action{0}graph{0}bundle".format(m.group(1))),
    ("S5a-interface", re.compile(r"\bISTARODK"), "IActionGraphBundle"),
    ("S5b-interface", re.compile(r"\bISTAR(?=[A-Z])"), "IActionGraphBundle"),
    ("S5-type", re.compile(r"\b(?:STARODK|StarOdk|StarODK|Starodk)"), "ActionGraphBundle"),
    # Glued after a lowercase verb: updateSTARODK -> updateActionGraphBundle (the shipped SDK name).
    ("S5c-glued-type", re.compile(r"(?<=[a-z])STARODK"), "ActionGraphBundle"),
    ("S5d-glued-prefix", re.compile(r"(?<=[a-z])STAR(?=[A-Z][a-z])"), "ActionGraphBundle"),
    ("S6-route", re.compile(r"\bstarodk"), "actiongraphbundle"),
    # Wire/param names keep the full noun (starOdkId -> actionGraphBundleId); short locals become bundle*.
    ("S6a-camel", re.compile(r"(?<![A-Za-z])star(?:Odk|ODK)"), "actionGraphBundle"),
    ("S6b-camel", re.compile(r"(?<![A-Za-z])(_?)star(?=[A-Z])"), r"\1bundle"),
    # Explicit suffixes only: a bare Star(?=[A-Z]) would hit StarTable, which lowercases to "startable".
    ("S6c-pascal", re.compile(r"\bStar(?=Generate|Deploy|Manager|Store|Service|Controller|Dapp)"),
     "ActionGraphBundle"),
    ("S6d-embedded", re.compile(r"(?<=[a-z])Star(?=Store|Manager|Service|Controller)"), "ActionGraphBundle"),
    ("S7-type-prefix", re.compile(r"\bSTAR(?=[A-Z][a-z])"), "ActionGraphBundle"),
    ("S8-prose", re.compile(r"\bSTAR\b"), "Action Graph Bundle"),
    ("S9-pascal-suffix", re.compile(r"(?<=[a-z])ODK(?![A-Za-z])"), "Bundle"),
    ("S10-pascal-suffix", re.compile(r"(?<=[a-z])Odk(?=s?(?![a-z]))"), "Bundle"),
    ("S9b-glued-suffix", re.compile(r"(?<=[A-Z0-9])ODK(?=s?(?![A-Za-z]))"), "Bundle"),
    ("S11-prose", re.compile(r"(?<![A-Za-z])ODK(s?)(?![A-Za-z])"), _odk_prose),
    ("S12-identifier", re.compile(r"(?<![A-Za-z])Odk(?=s?(?![a-z]))"), "Bundle"),
    ("S13-identifier", re.compile(r"(?<![A-Za-z])odk(?=s?(?![a-z]))"), "bundle"),
]

# Same guards as d28_rename.py: no letter before (request, conquest), no letter after but a plural s (question).
NOUN_RULES = [
    ("N1-quest", re.compile(r"(?<![A-Za-z])quest(?=s?(?![a-z]))"), "action"),
    ("N2-quest", re.compile(r"(?<![A-Za-z])Quest(?=s?(?![a-z]))"), "Action"),
    ("N3-quest", re.compile(r"(?<![A-Za-z])QUEST(?=S?(?![A-Z]))"), "ACTION"),
    ("N4-avatar", re.compile(r"(?<![A-Za-z])avatar(?=s?(?![a-z]))"), "user"),
    ("N5-avatar", re.compile(r"(?<![A-Za-z])Avatar(?=s?(?![a-z]))"), "User"),
    ("N6-avatar", re.compile(r"(?<![A-Za-z])AVATAR(?=S?(?![A-Z]))"), "USER"),
]

# Applied only to lines another rule changed: "a quest" -> "a action" -> "an action".
ARTICLE_RULES = [
    ("A1-article", re.compile(r"\b([Aa]) (?=[*_`]*(?:action|Action|ACTION)\b)"), lambda m: m.group(1) + "n "),
    ("A2-article", re.compile(r"\b([Aa])n (?=[*_`]*(?:user|User|USER|bundle|Bundle)\b)"),
     lambda m: m.group(1) + " "),
]

# Prose rewrites, not token swaps (track spec AC6). Each `old` must occur exactly once.
MANUAL_EDITS = [
    ("G1-domain-card", "frontend/src/app/page.tsx",
     "    title: 'Actions in games',\n"
     "    body: 'Player journeys with real stakes — earn, trade, unlock. The same engine that moves money moves play, without a separate stack.',",
     "    title: 'Community governance',\n"
     "    body: 'Proposals, votes and mandates with real consequences — decide, fund, deliver. The same engine that moves money carries out what a community decides, without a separate stack.',"),
    ("G2-hero-lede", "frontend/src/app/page.tsx",
     "operational processes — even actions in games — as living graphs of action.",
     "operational processes and community governance as living graphs of action."),
    ("G3-ticker", "frontend/src/app/page.tsx",
     "<span>GOVERNANCE</span><span>/</span><span>PLAY</span>",
     "<span>GOVERNANCE</span>"),
    ("G4-domains-note", "frontend/src/app/page.tsx",
     '<span className="az-accent">a game action can pay a supplier</span>',
     '<span className="az-accent">a community vote can pay a supplier</span>'),
    ("G5-holon-example", "frontend/src/app/architecture/page.tsx",
     "This is what makes a game reward, a material credit, and a payroll line",
     "This is what makes a grant disbursement, a material credit, and a payroll line"),
    ("G6-bundle-example", "frontend/src/app/architecture/page.tsx",
     "A new marketplace, a co-op ledger, a game economy — scaffolded",
     "A new marketplace, a co-op ledger, a community fund — scaffolded"),
    ("G7-idiom", "frontend/src/components/action-builder/dag-warnings.ts",
     'the common "why won\'t my action save" guessing game.',
     'the common "why won\'t my action save" guesswork.'),
    ("G8-test-attributes", "tests/AZOA.WebAPI.IntegrationTests/Controllers/UserNFTControllerIntegrationTests.cs",
     '{ { "level", "1" }, { "karma", "100" } }',
     '{ { "tier", "1" }, { "score", "100" } }'),
    ("G9-star-icon", "frontend/src/components/layout/sidebar.tsx",
     "{ href: '/actions', label: 'Actions', icon: Star },\n"
     "      { href: '/action-graph-bundle', label: 'Action Graph Bundles', icon: Star },",
     "{ href: '/actions', label: 'Actions', icon: Workflow },\n"
     "      { href: '/action-graph-bundle', label: 'Action Graph Bundles', icon: Package },"),
    ("G9-star-icon", "frontend/src/components/layout/sidebar.tsx",
     "  Star,\n",
     "  Package,\n  Workflow,\n"),
]

PATH_RENAMES = [
    ("P1-archive-dir", "conductor/tracks/_archive/star-odk-ecosystem-tree",
     "conductor/tracks/_archive/action-graph-bundle-ecosystem-tree"),
    ("P2-archive-dir", "conductor/tracks/_archive/star-api",
     "conductor/tracks/_archive/action-graph-bundle-api"),
]


def read_text(path: Path) -> str:
    with path.open(encoding="utf-8", newline="") as handle:
        return handle.read()


def write_text(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def partition_of(rel: str) -> str:
    if rel.startswith("tests/"):
        return "tests"
    if rel.startswith(("frontend/", "sdk/")):
        return "frontend-sdk"
    if rel.startswith(("conductor/tracks/_archive/", "conductor/retros/")):
        return "docs-archive"
    if rel.startswith(("conductor/", "docs/")) or rel.endswith(".md"):
        return "docs-live"
    return "backend"


def tracked_files() -> list[str]:
    out = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True)
    files = []
    for rel in out.stdout.splitlines():
        path = Path(rel)
        if path.suffix.lower() not in TEXT_EXTENSIONS or rel.endswith("package-lock.json"):
            continue
        if rel in EXCLUDED_FILES or rel.startswith(EXCLUDED_PREFIXES):
            continue
        if EXCLUDED_PARTS.intersection(path.parts):
            continue
        files.append(rel)
    return files


def rewrite_line(line: str, rules: list) -> tuple[str, list[dict]]:
    hits = []
    for rule_id, pattern, replacement in rules:
        def record(m: re.Match, rule_id=rule_id, replacement=replacement) -> str:
            new = replacement(m) if callable(replacement) else m.expand(replacement)
            hits.append({"rule": rule_id, "from": m.group(0), "to": new})
            return new
        line = pattern.sub(record, line)
    return line, hits


def sweep_file(rel: str) -> tuple[str, str, list[dict]]:
    original = read_text(ROOT / rel)
    rules = STAR_RULES + ([] if rel.startswith(NOUN_RULES_SKIP_PREFIXES) else NOUN_RULES)
    out_lines, records = [], []
    for number, line in enumerate(original.splitlines(keepends=True), start=1):
        new_line, hits = rewrite_line(line, rules)
        historical = any(rel == file and marker in line for file, marker in HISTORICAL_LINES)
        if hits and (historical or PROTECTED_LINE.search(line)):
            records.append({"file": rel, "line": number, "kind": "protected", "rules": hits,
                            "before": line.rstrip("\r\n"), "after": line.rstrip("\r\n")})
            out_lines.append(line)
            continue
        if hits:
            new_line, article_hits = rewrite_line(new_line, ARTICLE_RULES)
            hits += article_hits
            records.append({"file": rel, "line": number, "rules": hits,
                            "before": line.rstrip("\r\n"), "after": new_line.rstrip("\r\n")})
        out_lines.append(new_line)
    return original, "".join(out_lines), records


def apply_manual_edits(texts: dict[str, str], records: list[dict]) -> None:
    for rule_id, rel, old, new in MANUAL_EDITS:
        if rel not in texts:
            texts[rel] = read_text(ROOT / rel)
        text = texts[rel]
        eol_old = old.replace("\n", "\r\n") if "\r\n" in text else old
        eol_new = new.replace("\n", "\r\n") if "\r\n" in text else new
        count = text.count(eol_old)
        if count == 0:
            continue  # applied by an earlier pass (and possibly amended in review since)
        if count != 1:
            raise SystemExit(f"{rule_id}: expected exactly one occurrence in {rel}, found {count}")
        line = text[: text.index(eol_old)].count("\n") + 1
        texts[rel] = text.replace(eol_old, eol_new)
        records.append({"file": rel, "line": line, "rules": [{"rule": rule_id, "from": old, "to": new}],
                        "before": old, "after": new})


def run(write: bool) -> list[dict]:
    texts, records = {}, []
    for rel in tracked_files():
        original, rewritten, file_records = sweep_file(rel)
        if file_records:
            texts[rel] = rewritten
            records += file_records
    apply_manual_edits(texts, records)
    records.sort(key=lambda r: (partition_of(r["file"]), r["file"], r["line"]))
    for index, record in enumerate(records, start=1):
        record["id"] = f"{ID_PREFIX}R{index:04d}"
        record["partition"] = partition_of(record["file"])
        record["rules_sha256"] = RULES_SHA256
    if write:
        for rel, text in texts.items():
            write_text(ROOT / rel, text)
    path_records = []
    for rule_id, source, target in PATH_RENAMES:
        if not (ROOT / source).exists():
            continue  # moved by an earlier pass; logging it again would claim a move that did not happen
        if write:
            subprocess.run(["git", "mv", source, target], cwd=ROOT, check=True)
        path_records.append({"id": f"{ID_PREFIX}{rule_id}", "partition": "docs-archive", "kind": "path",
                             "from": source, "to": target, "rules_sha256": RULES_SHA256})
    with MANIFEST.open("w", encoding="utf-8", newline="\n") as handle:
        for record in records + path_records:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
    return records


def main() -> int:
    command = COMMAND
    if command == "apply" and (len(sys.argv) < 3 or MANIFEST.exists()):
        print(f"apply needs a NEW manifest name; {MANIFEST.name} would overwrite an audit trail")
        return 2
    if command == "verify":
        found = [r for rel in tracked_files() for r in sweep_file(rel)[2]]
        remaining = [r for r in found if r.get("kind") != "protected"]
        for r in remaining:
            print(f"{r['file']}:{r['line']}: {[h['rule'] for h in r['rules']]} {r['before'][:120]}")
        print(f"{len(remaining)} line(s) would still change; {len(found) - len(remaining)} protected")
        return 1 if remaining else 0
    if command not in {"plan", "apply"}:
        print(__doc__)
        return 2
    records = run(write=command == "apply")
    tally: dict[tuple[str, str], int] = {}
    for r in records:
        key = (r["partition"], r.get("kind", "changed"))
        tally[key] = tally.get(key, 0) + 1
    print(f"{len(records)} manifest line(s) -> {MANIFEST.relative_to(ROOT)}")
    for (name, kind), count in sorted(tally.items()):
        print(f"  {name:13} {kind:9} {count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
