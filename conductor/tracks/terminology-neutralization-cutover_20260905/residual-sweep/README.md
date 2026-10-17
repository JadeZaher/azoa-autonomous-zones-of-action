---
type: Track Artifact
title: Retired-vocabulary residual sweep
description: Scripted find/replace of the Quest/Avatar/STAR/game vocabulary that survived D28 and the 10f4c16 STAR rename, with a per-line manifest and partitioned agent review.
tags: [terminology-neutralization-cutover_20260905, residual-sweep, review]
timestamp: 2026-10-16T00:00:00Z
---

# Retired-vocabulary residual sweep

D28 (`Avatar`→`User`, `Quest`→`Action`) and `10f4c16` (`STAR`/`STARODK`→`ActionGraphBundle`)
shipped on local `main`, but a later sweep found residue: the `odk` identifier family, `star-odk`
track ids, `STAR` in docs and live-test fixtures, `quest`/`avatar` in code written after the
rename, and game-flavoured landing copy. `sweep.py` fixes the token-shaped residue and logs
every changed line to `manifest.jsonl`. Reviewers then judge each line by hand.

## Run

```
python sweep.py verify                        # 0 lines left = idempotent; the check to re-run
python sweep.py plan                          # dry run -> manifest.plan.jsonl (scratch)
python sweep.py apply manifest-pass3.jsonl    # a new pass; refuses to overwrite an existing manifest
```

**Pass history.** `manifest.jsonl` (R0001–R0523) came from an earlier rule revision.
Its S13 had no plural `s`, and S5c/S5d/S9b did not exist yet. Review found those gaps.
`manifest-pass2.jsonl` (P2-…) holds the 17 lines the corrected rules then changed.
Records written from now on carry `rules_sha256`, a hash of the `sweep.py` that
produced them. `HISTORICAL_LINES` pins the review's two deliberate reverts (a
released tag name and a quoted finding), so a later pass cannot undo them.

## Rules

| Id | Shape | Becomes |
|---|---|---|
| S1/S2 | track ids `star-odk-ecosystem-tree`, `star-api` | `action-graph-bundle-ecosystem-tree`, `action-graph-bundle-api` (dirs renamed by P1/P2) |
| S3 | prose `STAR ODK` / `Star-ODK` | `Action Graph Bundle` |
| S4 | slugs `star-odk`, `star_odk` | `action-graph-bundle`, `action_graph_bundle` |
| S5/S5a/S5b/S6d | types `STARODK`, `StarOdk`, `ISTARODK`, `ISTAR*`, `…StarStore` | `ActionGraphBundle`, `IActionGraphBundle*` |
| S6 | route `starodk` | `actiongraphbundle` |
| S6a/S6b | camel `starOdkId`, `_starManager`, `starId` | `actionGraphBundleId`, `_bundleManager`, `bundleId` |
| S6c | `Star` + `Generate/Deploy/Manager/Store/Service/Controller/Dapp` only | `ActionGraphBundle…`. Never a bare `Star[A-Z]`: `StarTable` lowercases to "startable" |
| S7/S8 | `STAR` type prefix / standalone prose | `ActionGraphBundle` / `Action Graph Bundle` |
| S9–S13 | `ODK`/`Odk`/`odk` suffix, prose, identifier | `Bundle` / `bundle` |
| N1–N6 | `quest`/`avatar` in all three casings, d28 guards (no letter before, only a plural `s` after) | `action` / `user` |
| A1/A2 | article agreement on changed lines only | `a action`→`an action`, `an user`→`a user` |
| G1–G9 | hand-written prose rewrites (spec AC6), exact-match, applied once | see `MANUAL_EDITS` in `sweep.py` |

## Exclusions

Each of these documents the rename, is generated, or is mirrored byte-for-byte: `conductor/RUNBOOK.md`,
this track directory, `scripts/d28_rename.py`, `SurrealDeploymentLeastPrivilegeContractTests.cs`,
`AttributePocoByteEquivalenceTests.cs` (golden-guard history), `ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`
(byte-identical with ArdaNova), and `Persistence/SurrealDb/Generated/**` (regenerated with
`AZOA_REGENERATE_GOLDENS=1`, never hand-edited). The Quest/Avatar rules also skip consumer
vocabulary (`conductor/product.md`, the ArdaNova readiness-waves track) and the third-party shadcn
`Avatar` primitive (`ui/avatar.tsx`, `layout/header.tsx`). Lines narrating a rename
(`Avatar→User`, `star_odk -> action_graph_bundle`) are logged as `protected` and left untouched.

**Out of scope:** `Holon`. It is OASIS-derived but it is a core primitive (table, route, SDK, both
consumers). Renaming it would be its own D28-scale decision.

## Review protocol

Each `manifest.jsonl` record carries a `partition` (`backend`, `tests`, `frontend-sdk`,
`docs-live`, `docs-archive`). One reviewer owns each partition and edits only its files. Line
numbers are pre-apply, and replacements were 1:1 per line, so they still hold (exception:
`sidebar.tsx` gained one import line after line 21). P1/P2 moved files out of `star-api/` and
`star-odk-ecosystem-tree/`.

Verdict per record goes in `review/<partition>.jsonl` as `{"id","verdict","note"}`:

- `keep`: the replacement is correct.
- `amend`: the intent is right but the form is wrong (grammar, casing, a half-renamed compound). Fixed in place.
- `revert`: the old word was legitimate (consumer vocabulary, a quoted historical identifier, a third-party symbol). Restored.
- `miss-fixed`: residue the rules did not catch. Logged with `file`, `line`, `before`, `after`.
- `question`: needs the owner. Left unedited.

Reviewers do not build or test. One integrated sweep runs after every partition reports.
