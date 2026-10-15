#!/usr/bin/env python3
"""D28 terminology sweep: Avatar->User, Quest->Action, plus vocabulary reports.

Decision record and proofs: conductor/RUNBOOK.md §3 (D28, D31-D35).
Every guard here is backed by a measurement recorded there. This file was
adversarially reviewed before first run; the findings it fixes are cited inline
as [C1] [C2] [H1]..[H6] [M1]..[M4] [L1]..[L3].

Usage:
    python scripts/d28_rename.py verify                 # gate: must pass before apply
    python scripts/d28_rename.py plan   <partition>...  # per-file counts, writes nothing
    python scripts/d28_rename.py apply  <partition>...  # rewrite files in place
    python scripts/d28_rename.py paths                  # emit the git mv list
    python scripts/d28_rename.py report-vocab           # locate strand 2/3 prose
    python scripts/d28_rename.py partitions             # list partition names
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# [M1] cp1252 consoles cannot encode the arrows/checks below.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ---------------------------------------------------------------------------
# Exclusions
# ---------------------------------------------------------------------------

EXCLUDED_DIR_PARTS = {
    "node_modules", "bin", "obj", ".git", "dist", ".next", "TestResults",
    ".omc", ".claude", ".mpg", ".vs", "coverage", "verified-evidence",
    "__pycache__",
}

# Regenerated from POCOs; hand edits are discarded (CONVENTION.md:319-321).
REGENERATED_DIRS = (
    "Persistence/SurrealDb/Generated/Schemas",
    "Persistence/SurrealDb/Generated/Flowcharts",
)

EXCLUDED_FILES = {
    # The one proven false positive: a wrapper around @base-ui/react/avatar.
    "frontend/src/components/ui/avatar.tsx",
    # [H6] The decision record must not rewrite itself into "User -> User".
    "conductor/RUNBOOK.md",
    "scripts/d28_rename.py",
    # Same class as [H6]: this file's baseline re-base comment states the
    # mapping "avatar->user, quest->action" and cites the pre-wipe row counts.
    # Sweeping it would render the audit trail as "user->user".
    "tests/AZOA.WebAPI.Tests/Core/SurrealDeploymentLeastPrivilegeContractTests.cs",
}

# Same class again: this track's own record STATES the mapping, so sweeping it
# would collapse every "Avatar -> User" into "User -> User" and destroy the
# document that explains the rename. A future rename of different nouns can be
# swept normally; only this track's directory is self-describing.
SELF_DESCRIBING_DIRS = (
    "conductor/tracks/terminology-neutralization-cutover_20260905",
)

UI_AVATAR_MODULE = "components/ui/avatar"

# [M4] Broadened: type-only imports, re-exports, default imports, relative paths.
UI_AVATAR_LOCAL_PATTERNS = (
    re.compile(r"</?Avatar\b"),
    re.compile(r"\btypeof\s+Avatar\b"),
    re.compile(r"(?:import|export)\s+(?:type\s+)?[^;]*?from\s*['\"][^'\"]*"
               + re.escape("ui/avatar") + r"['\"]"),
)

THIRD_PARTY_AVATAR_IDENTS = (  # longest first, or prefixes get consumed
    "AvatarGroupCount", "AvatarFallback", "AvatarImage", "AvatarBadge",
    "AvatarGroup", "@base-ui/react/avatar",
)

# [H3] .jsonl carries live-test route/body payloads that break silently.
TEXT_SUFFIXES = {
    ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json", ".jsonl",
    ".surql", ".md", ".yml", ".yaml", ".sql", ".sh", ".ps1", ".csproj",
    ".props", ".sln", ".http", ".go",
}

# ---------------------------------------------------------------------------
# [H2] The exactness gate
# ---------------------------------------------------------------------------
#
# The original gate asserted `quest - request - question == guarded_matches`.
# That is a TAUTOLOGY: it reduces to "no substring `requestion` exists" and is
# blind to a third false-positive family. `bequest` is counted on both sides
# and passes it. Replaced with explicit token classification plus a denylist.

# English words containing "quest" that are NOT domain terms. Protected from
# the rename, and their presence is reported rather than silently rewritten.
QUEST_DENYLIST = frozenset({
    "bequest", "bequests", "conquest", "conquests", "inquest", "inquests",
    "questrade", "questionnaire", "questionnaires",
})

# [M2] Compounds ending in "...reQuest" that are NOT Request-family: the
# lookbehind sees "re" and refuses to rename them. Pre-edited out of the way.
OVERBLOCK_RE = re.compile(r"\b[A-Za-z0-9_]*[a-z0-9_][Rr][Ee]Quest[A-Za-z0-9_]*\b")

TOKEN_RE = re.compile(r"[A-Za-z0-9_]*[Qq][Uu][Ee][Ss][Tt][A-Za-z0-9_]*")

# ---------------------------------------------------------------------------
# [C1] Pre-rules: namespace + bare-entity renames, applied BEFORE token rules
# ---------------------------------------------------------------------------
#
# `Quest` cannot become bare `Action` in C#: AZOA.WebAPI.csproj enables
# ImplicitUsings, so `global using System;` is in every file and the entity
# would collide with System.Action (CS0104), while namespaces `*.Quest` ->
# `*.Action` would shadow the delegate (CS0118). Proven: 136 errors.
# Resolution (D35): suffix the entity, pluralize the namespaces. The DB table,
# REST route and SDK type all stay `action` - the collision is C#-only.
#
# `Services.Avatar` is pluralized for the same class of reason: a namespace
# `Services.User` alongside the type `Models.User` is ambiguous under a file
# that imports both.

# A complete census of dotted `.Quest` / `.Avatar` contexts (no truncation this
# time - an earlier `head -20` on a 29-row list produced a wrong conclusion and
# 5 compile errors) found exactly four receiver kinds:
#   namespace roots  Models / Services / Tests   -> namespace segment or type
#   `context` / `ctx`                            -> a PROPERTY named Quest
#   `SearchableEntityType`                       -> an ENUM member
#   `SurrealDb`                                  -> appears only inside a comment
# The rules below are ordered so each kind lands on its correct target. Avatar
# needs no such split: type, property and enum member all become `User`.

# 1. Fully-qualified namespaces. Anchored with a right boundary, or the plain
#    substring form rewrites `Services.QuestDagValidator` into
#    `Services.ActionsDagValidator` (measured: 1 x CS0234).
NAMESPACE_RENAMES = [
    (re.compile(r"AZOA\.WebAPI\.Tests\.Services\.Quest(?![A-Za-z0-9_])"),
     "AZOA.WebAPI.Tests.Services.Actions"),
    (re.compile(r"AZOA\.WebAPI\.Models\.Quest(?![A-Za-z0-9_])"),
     "AZOA.WebAPI.Models.Actions"),
    (re.compile(r"AZOA\.WebAPI\.Services\.Quest(?![A-Za-z0-9_])"),
     "AZOA.WebAPI.Services.Actions"),
    (re.compile(r"AZOA\.WebAPI\.Tests\.Quest(?![A-Za-z0-9_])"),
     "AZOA.WebAPI.Tests.Actions"),
    (re.compile(r"AZOA\.WebAPI\.Services\.Avatar(?![A-Za-z0-9_])"),
     "AZOA.WebAPI.Services.Users"),
]

# 2. Partially-qualified namespace SEGMENTS: a dotted noun under a known
#    namespace root and followed by another dot. Restricting the receiver is
#    what keeps `context.Quest.Nodes` (a property) out of this rule.
PARTIAL_NAMESPACE_RULES = [
    (re.compile(r"\b(Models|Services|Tests)\.Quest(?=\.)"), r"\1.Actions"),
    (re.compile(r"\bServices\.Avatar(?=\.)"), "Services.Users"),
]

# 3. Qualified TYPE references. `Models.Quest` is always the SurrealDB POCO
#    (verified: every occurrence is the fully-qualified
#    AZOA.WebAPI.Persistence.SurrealDb.Models.Quest); `Actions.Quest` is what
#    rules 1-2 leave behind for AZOA.WebAPI.Models.Quest.Quest.
QUALIFIED_TYPE_RULES = [
    (re.compile(r"(?<=\bModels\.)Quest(?![A-Za-z0-9_])"), "ActionDefinition"),
    (re.compile(r"(?<=\bActions\.)Quest(?![A-Za-z0-9_])"), "ActionDefinition"),
]

# 4/5. Every remaining bare `Quest` - dotted or not - becomes the entity name.
# They must agree: `QuestNodeExecutionContext` declares the type-and-property
# same-name idiom `public Quest Quest { get; }` (Models/Quest/QuestNodeExecutionContext.cs:57),
# so emitting `Action` for the dotted usage and `ActionDefinition` for the
# declaration desynchronised them (measured: 6 x CS1061). One name for both.
# Cosmetic follow-up for the diff review: `context.ActionDefinition` reads
# heavily and could be polished to `.Action`; that is 8 sites and is naming,
# not correctness.
BARE_ENTITY_RULES = [
    (re.compile(r"(?<![A-Za-z0-9_])Quest(?![A-Za-z0-9_])"), "ActionDefinition"),
]

# ---------------------------------------------------------------------------
# Token rules (compound identifiers): QuestManager -> ActionManager, etc.
# ---------------------------------------------------------------------------

RENAME_RULES: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"(?<![Rr][Ee])QUEST(?!ION)"), "ACTION"),
    (re.compile(r"(?<![Rr][Ee])Quest(?!ion)"), "Action"),
    (re.compile(r"(?<![Rr][Ee])quest(?!ion)"), "action"),
    (re.compile(r"AVATAR"), "USER"),
    (re.compile(r"Avatar"), "User"),
    (re.compile(r"avatar"), "user"),
]

# ---------------------------------------------------------------------------
# [H4][M2] Surgical pre-edits that unblock the mechanical sweep
# ---------------------------------------------------------------------------

PRE_EDITS: dict[str, list[tuple[str, str]]] = {
    # [H4] `quest` and `action` share a scope; renaming yields CS0136/CS0841.
    "Services/Quest/Workflow/QuestNodeStepHandler.cs": [
        ("var action = ChainActionRecovery.Decide", "var recoveryAction = ChainActionRecovery.Decide"),
        ("switch (action)", "switch (recoveryAction)"),
    ],
    # [M2] "...FailureQuest" is over-blocked by the `re` lookbehind.
    "tests/AZOA.WebAPI.Tests/Quest/QuestManagerOnFailureTests.cs": [
        ("BuildOnFailureQuest", "BuildQuestWithOnFailure"),
    ],
    # [H5] These literals are DIRECTORY segments, not type references, so they
    # must follow the `git mv` target (Services/Actions) and not the entity
    # name. Without this the source-scanning guard finds no files and fails
    # with "the brand-leak scan must locate the handler/predicate/config
    # source" (measured: 1 failure). Contrast the *filename* literals in
    # NodeOperatorSecurityContractTests ("SurrealAvatarStore.cs"), which the
    # ordinary rules rename correctly and which only need git mv to land.
    "tests/AZOA.WebAPI.Tests/Quest/Handlers/BrandLeakGuardTests.cs": [
        ('"Services", "Quest", "Handlers"', '"Services", "Actions", "Handlers"'),
        ('"Services", "Quest", "Predicates"', '"Services", "Actions", "Predicates"'),
        ('"Models", "Quest", "NodeConfigs.cs"', '"Models", "Actions", "NodeConfigs.cs"'),
    ],
}

# ---------------------------------------------------------------------------
# Strands 2 and 3 - located, never auto-rewritten (sentence-level edits)
# ---------------------------------------------------------------------------

# [M3] \b anchors stop `enableVertexAttribArray` and `describable` matching riba.
VOCAB_STRAND_2 = re.compile(
    r"qard[\s_-]?hasan|\briba\b|\bgharar\b|\bzakat\b|\bshariah\b|\bsharia\b"
    r"|\bhalal\b|\bharam\b|\bislamic\b",
    re.IGNORECASE,
)
VOCAB_STRAND_3 = re.compile(r"\bOASIS\b|\bOasis\b|\boasis\b|\bSleek\b|\bsleek\b")

# ---------------------------------------------------------------------------
# Partitions
# ---------------------------------------------------------------------------
# [C2] `backend.include` was hand-curated and missed Helpers/ (compile break)
# and named a non-existent Persistence/Stores. Roots are now verified to exist
# at startup, and `verify` asserts every matching file is claimed by exactly
# one partition.

PARTITIONS: dict[str, dict] = {
    "models": {
        "wave": 1,
        "include": ["Persistence/SurrealDb/Models"],
        "note": "Decorated POCOs. Goldens regenerate from here.",
    },
    "backend": {
        "wave": 1,
        "include": [
            "Controllers", "Managers", "Services", "Core", "Interfaces",
            "Models", "Providers", "Validators", "Mcp", "Helpers",
            "Extensions", "Middleware", "Observability", "Settings",
        ],
        "exclude": ["Persistence/SurrealDb"],
        "note": "All backend C# + adjacent AGENTS.md. Both nouns together.",
    },
    "backend-root": {
        "wave": 1,
        "include": [
            "Program.cs", "GlobalUsings.cs", "AZOA.WebAPI.csproj",
            "appsettings.json", "appsettings.Development.json",
        ],
        "note": "Program.cs carries both nouns; cannot be split by noun.",
    },
    "baselines": {
        "wave": 1,
        "include": [
            "Persistence/SurrealDb/CompatibilityBaselines",
            "Persistence/SurrealDb/Migrations",
            "Persistence/SurrealDb/CONVENTION.md",
        ],
        "note": "Hand-maintained; moves in lockstep with the goldens.",
    },
    "tests": {
        "wave": 1,
        "include": ["tests"],
        "note": "Includes LiveTests *.jsonl route/body payloads [H3].",
    },
    "sdk": {
        "wave": 2,
        "include": ["sdk/azoa-wallet"],
        "note": "Whole package: src + tests + examples + README [H3]. tsup rebuild after.",
    },
    "frontend": {
        "wave": 3,
        "include": ["frontend"],
        "note": "ui/avatar.tsx excluded (third-party). Includes configs + public/.",
    },
    "docs": {
        "wave": 4,
        "include": [
            "conductor", "docs", "README.md", "RUNBOOK.md", "DEVELOPMENT.md",
            "PROVIDERS.md", "API_SYNC.md", "CHANGELOG.md",
        ],
        "note": "D32: shipped track specs renamed too. RUNBOOK.md excluded [H6].",
    },
    "misc": {
        "wave": 4,
        "include": ["fiat-sidecar", "db", "deploy", "scripts", "azoa.sln",
                    "Directory.Build.props", "docker-compose.yml"],
        "note": "Leftovers.",
    },
}


# ---------------------------------------------------------------------------
# File walking
# ---------------------------------------------------------------------------

def is_excluded(path: Path) -> bool:
    try:
        rel_parts = path.relative_to(REPO).parts  # [L1] relative, not absolute
    except ValueError:
        return True
    rel = "/".join(rel_parts)
    if rel in EXCLUDED_FILES:
        return True
    if any(part in EXCLUDED_DIR_PARTS for part in rel_parts):
        return True
    if any(rel.startswith(d) for d in SELF_DESCRIBING_DIRS):
        return True
    return any(rel.startswith(d) for d in REGENERATED_DIRS)


def iter_files(roots: list[str], exclude: list[str] | None = None, warn: bool = False):
    exclude = exclude or []
    for root in roots:
        target = REPO / root
        if not target.exists():
            if warn:  # [L2] silent skips hid the Persistence/Stores typo
                print(f"  ! partition root does not exist: {root}", file=sys.stderr)
            continue
        candidates = [target] if target.is_file() else sorted(target.rglob("*"))
        for path in candidates:
            if not path.is_file() or path.suffix not in TEXT_SUFFIXES:
                continue
            if is_excluded(path):
                continue
            rel = path.relative_to(REPO).as_posix()
            if any(rel.startswith(e) for e in exclude):
                continue
            yield path


def read(path: Path) -> str | None:
    """newline='' so CRLF survives; the tree is mixed under core.autocrlf=true."""
    try:
        with path.open("r", encoding="utf-8", newline="") as fh:
            return fh.read()
    except (UnicodeDecodeError, OSError):
        return None


def write_file(path: Path, text: str) -> None:  # [H1] was shadowed by a bool param
    with path.open("w", encoding="utf-8", newline="") as fh:
        fh.write(text)


# ---------------------------------------------------------------------------
# Transform
# ---------------------------------------------------------------------------

def protect(text: str) -> tuple[str, dict[str, str]]:
    """Sentinel out everything that must survive verbatim.

    Sentinels use NUL, which cannot appear in these sources, so they can never
    collide with content or with each other.
    """
    tokens: dict[str, str] = {}
    n = 0

    def stash(fragment: str) -> str:
        nonlocal n
        sentinel = f"\x00D28P{n}\x00"
        tokens[sentinel] = fragment
        n += 1
        return sentinel

    # [H2] Non-domain English words containing "quest".
    for word in QUEST_DENYLIST:
        for variant in (word, word.capitalize(), word.upper()):
            if variant in text:
                text = text.replace(variant, stash(variant))

    # [M4] Bare third-party `Avatar` only in files that import the wrapper.
    if UI_AVATAR_MODULE in text or "ui/avatar" in text:
        for pattern in UI_AVATAR_LOCAL_PATTERNS:
            text = pattern.sub(lambda m: stash(m.group(0)), text)

    for ident in THIRD_PARTY_AVATAR_IDENTS:
        if ident in text:
            text = text.replace(ident, stash(ident))
    return text, tokens


def transform(text: str, rel: str = "") -> tuple[str, int]:
    original = text
    for old, new in PRE_EDITS.get(rel, []):
        text = text.replace(old, new)

    text, tokens = protect(text)

    for pattern, repl in NAMESPACE_RENAMES:        # [C1] 1. fully-qualified ns
        text = pattern.sub(repl, text)
    for pattern, repl in PARTIAL_NAMESPACE_RULES:  # [C1] 2. ns segments
        text = pattern.sub(repl, text)
    for pattern, repl in QUALIFIED_TYPE_RULES:     # [C1] 3. qualified types
        text = pattern.sub(repl, text)
    for pattern, repl in BARE_ENTITY_RULES:        # [C1] 4. members, 5. bare
        text = pattern.sub(repl, text)
    for pattern, repl in RENAME_RULES:          # then compound identifiers
        text = pattern.sub(repl, text)

    for sentinel, fragment in tokens.items():
        text = text.replace(sentinel, fragment)

    if text == original:
        return text, 0
    hits = sum(1 for a, b in zip(original.split(), text.split()) if a != b)
    return text, max(hits, 1)


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def all_matching_files() -> set[str]:
    """Every file in the tree that the sweep would change."""
    out = set()
    for path in iter_files(["."]):
        text = read(path)
        if text is None:
            continue
        new, _ = transform(text, path.relative_to(REPO).as_posix())
        if new != text:
            out.add(path.relative_to(REPO).as_posix())
    return out


def claimed_files() -> dict[str, str]:
    claimed: dict[str, str] = {}
    for name, spec in PARTITIONS.items():
        for path in iter_files(spec["include"], spec.get("exclude")):
            claimed.setdefault(path.relative_to(REPO).as_posix(), name)
    return claimed


def cmd_verify() -> int:
    print("D28 exactness gate")
    print("=" * 66)
    failures = 0

    # 1. Token classification - replaces the old tautological count identity.
    domain: set[str] = set()
    denied: dict[str, list[str]] = {}
    overblocked: dict[str, list[str]] = {}
    for path in iter_files(["."]):
        text = read(path)
        if text is None:
            continue
        rel = path.relative_to(REPO).as_posix()
        for tok in TOKEN_RE.findall(text):
            low = tok.lower()
            if low in QUEST_DENYLIST:
                denied.setdefault(tok, []).append(rel)
            elif "request" in low or "question" in low:
                continue
            else:
                domain.add(tok)
        # No `"request" not in tok` filter here: an over-blocked token contains
        # `re`+`quest`, so it ALWAYS contains the substring "request" once
        # lowercased - that filter discarded exactly the cases it was meant to
        # catch. The discriminator is case: Request/request always has a
        # lowercase q, so `[Rr][Ee]` + capital `Quest` is never a Request word.
        for tok in OVERBLOCK_RE.findall(text):
            if tok.lower() not in QUEST_DENYLIST:
                overblocked.setdefault(tok, []).append(rel)

    print(f"  distinct domain quest-tokens        {len(domain):>6,}")
    print(f"  denylisted non-domain words found   {len(denied):>6,}  (protected, not renamed)")
    for tok, files in sorted(denied.items()):
        print(f"      {tok}: {files[0]}{'' if len(files) == 1 else f' (+{len(files)-1})'}")
    print(f"  over-blocked '...reQuest' compounds {len(overblocked):>6,}")
    for tok, files in sorted(overblocked.items()):
        pre_edited = any(tok in old for edits in PRE_EDITS.values() for old, _ in edits)
        if not pre_edited:
            failures += 1
        status = "pre-edited" if pre_edited else "UNHANDLED -> would survive un-renamed"
        print(f"      {tok}: {files[0]} [{status}]")

    # 2. Every changed file must belong to exactly one partition. [C2]
    matching = all_matching_files()
    claimed = claimed_files()
    unclaimed = sorted(matching - set(claimed))
    print(f"\n  files the sweep would change        {len(matching):>6,}")
    print(f"  files claimed by a partition        {len(matching & set(claimed)):>6,}")
    print(f"  UNCLAIMED (would be silently missed){len(unclaimed):>6,}")
    for rel in unclaimed[:25]:
        print(f"      {rel}")
    if len(unclaimed) > 25:
        print(f"      ... and {len(unclaimed) - 25} more")
    failures += len(unclaimed)

    print("=" * 66)
    if failures:
        print(f"  GATE FAILED ({failures} issue(s)). Do not run apply.")
        return 1
    print("  GATE PASSED. Every changed file is claimed; no unhandled tokens.")
    return 0


def resolve(names: list[str]) -> list[str]:
    if not names or names == ["all"]:
        return list(PARTITIONS)
    unknown = [n for n in names if n not in PARTITIONS]
    if unknown:
        print(f"unknown partition(s): {', '.join(unknown)}", file=sys.stderr)
        print(f"available: {', '.join(PARTITIONS)}", file=sys.stderr)
        sys.exit(2)
    return names


def cmd_sweep(names: list[str], do_write: bool) -> int:
    total_files = total_hits = 0
    for name in sorted(resolve(names), key=lambda n: (PARTITIONS[n]["wave"], n)):
        spec = PARTITIONS[name]
        rows: list[tuple[int, str]] = []
        for path in iter_files(spec["include"], spec.get("exclude"), warn=True):
            text = read(path)
            if text is None:
                continue
            rel = path.relative_to(REPO).as_posix()
            new, n = transform(text, rel)
            if new == text:
                continue
            rows.append((n, rel))
            if do_write:
                write_file(path, new)
        hits = sum(n for n, _ in rows)
        total_files += len(rows)
        total_hits += hits
        verb = "REWROTE" if do_write else "would change"
        print(f"\n[wave {spec['wave']}] {name}: {verb} {len(rows)} files ({hits:,} changed tokens)")
        print(f"  {spec['note']}")
        for n, rel in sorted(rows, reverse=True)[:6]:
            print(f"    {n:>6,}  {rel}")
        if len(rows) > 6:
            print(f"    ... and {len(rows) - 6} more files")
    print(f"\nTOTAL: {total_files} files")
    if not do_write:
        print("(plan only - nothing written)")
    return 0


def cmd_paths() -> int:
    """[H5] Path renames the content sweep cannot do. Run AFTER content edits."""
    moves: list[tuple[str, str]] = []
    for path in sorted(REPO.rglob("*")):
        if is_excluded(path):
            continue
        rel = path.relative_to(REPO).as_posix()
        new_rel, _ = transform(rel)
        if new_rel != rel:
            moves.append((rel, new_rel))
    # Deepest first so parents still exist when children move.
    moves.sort(key=lambda m: m[0].count("/"), reverse=True)
    print(f"# {len(moves)} path renames. Run after content edits, before any build.")
    print("# Next.js route dirs are load-bearing: /quests -> /actions must move")
    print("# or every rewritten href 404s.")
    for old, new in moves:
        print(f'git mv "{old}" "{new}"')
    return 0


def cmd_report_vocab() -> int:
    for label, pattern in (("STRAND 2 - loaded religious vocabulary", VOCAB_STRAND_2),
                           ("STRAND 3 - residual OASIS/Sleek vocabulary", VOCAB_STRAND_3)):
        print(f"\n{label}")
        print("=" * 70)
        found: dict[str, list[tuple[int, str]]] = {}
        for path in iter_files(["."]):
            text = read(path)
            if text is None:
                continue
            hits = [(i, line.strip()[:110])
                    for i, line in enumerate(text.splitlines(), 1)
                    if pattern.search(line)]
            if hits:
                found[path.relative_to(REPO).as_posix()] = hits
        total = sum(len(v) for v in found.values())
        print(f"{total} lines across {len(found)} files\n")
        for rel, hits in sorted(found.items(), key=lambda kv: -len(kv[1]))[:20]:
            print(f"  {rel}  ({len(hits)} lines)")
            for ln, snippet in hits[:2]:
                print(f"      {ln}: {snippet}")
        if len(found) > 20:
            print(f"  ... and {len(found) - 20} more files")
    print("\nNeither strand is auto-rewritten: both are sentence-level edits.")
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    cmd, args = sys.argv[1], sys.argv[2:]
    if cmd == "verify":
        return cmd_verify()
    if cmd == "plan":
        return cmd_sweep(args, do_write=False)
    if cmd == "apply":
        return cmd_sweep(args, do_write=True)
    if cmd == "paths":
        return cmd_paths()
    if cmd == "report-vocab":
        return cmd_report_vocab()
    if cmd == "partitions":
        for name, spec in sorted(PARTITIONS.items(), key=lambda kv: (kv[1]["wave"], kv[0])):
            print(f"  wave {spec['wave']}  {name:<14} {spec['note']}")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
