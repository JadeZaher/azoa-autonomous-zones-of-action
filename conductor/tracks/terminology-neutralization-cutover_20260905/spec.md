---
type: Track Spec
title: Terminology neutralization cutover
description: Rename AZOA's domain nouns to neutral platform vocabulary (Avatar→User, Quest→Action), strip consumer-specific language, and carry the breaking change through both consumer repos.
tags: [feature, terminology-neutralization-cutover_20260905, in-flight, breaking-change, cross-repo]
timestamp: 2026-09-05T00:00:00Z
resource: ./metadata.json
---

# Terminology neutralization cutover

## Overview

AZOA is the neutral value/workflow substrate; consumer-specific vocabulary
belongs in the consumer, not the platform. This track renames the platform's
domain nouns, strips loaded vocabulary from AZOA's own primitives, and carries
the resulting breaking change through the two consumer repositories.

Decision record: `conductor/RUNBOOK.md` §3 — **D28** (the rename), **D31**
(wipe-and-reseed instead of a record-ID migration), **D32** (rename historical
track specs too), **D33** (scripted transform reviewed by agents), **D34**
(Taawun handling), **D35** (the `ActionDefinition` C# collision resolution).

## Why this is not a mechanical find/replace

Four properties made this a designed change rather than a sweep:

1. **`Quest` is a substring of `Request`.** 6,507 of 20,592 `quest` occurrences
   are `request`-family and 45 are `question`-family. The guard is
   `(?<![Rr][Ee])quest(?!ion)`, and the case discriminator matters:
   `Request`/`request` always carries a lowercase `q`, so `re` + capital `Quest`
   is never a Request word.
2. **`Action` collides with `System.Action`.** `AZOA.WebAPI.csproj` enables
   `ImplicitUsings`, so `global using System;` reaches every file. The literal
   rename produced **136 compile errors**. Resolved by D35: the C# entity is
   `ActionDefinition` and namespaces are plural (`Models.Actions`), while the
   **DB table, REST route and SDK type all stay `action`**.
3. **SurrealDB record IDs embed the table name.** ~69 FK columns
   (60 `record<avatar>` across 35 tables, 9 `record<quest>` across 7) hold
   values like `avatar:abc123`. D31 drops the remap migration because the live
   database held 6 `avatar` rows and 2 `api_key` rows and nothing else.
4. **Two consumers depend on the wire contract**, one of which breaks at
   runtime rather than compile time.

## Scope

**In scope**
- `Avatar` → `User`, `Quest` → `Action` across C# models/managers/controllers,
  SurrealDB tables and regenerated schemas, REST routes, the TypeScript SDK, the
  frontend, tests, and documentation including shipped track specs (D32).
- Strand 2 — strip loaded religious vocabulary from AZOA's own primitives. *The
  constraints stay, the vocabulary goes.* "Qard Hasan tracker" becomes a
  **principal-only loan tracker** whose defining property is that no
  interest-accrual primitive exists; "riba-free" becomes "no interest-bearing
  primitive may exist in a flow"; "anti-gharar" becomes "ambiguity must fail
  closed and refund". Zakat is a consumer-side calculation over AZOA facts.
- Strand 3 — strip residual OASIS-project vocabulary.
- Both consumer repos, and the byte-identical
  `ARDANOVA-AZOA-INTEGRATION-CONTRACT.md` held in AZOA and ArdaNova.

**Out of scope**
- Any behavior change. This track must be provably behavior-preserving.
- The record-ID forward migration (D31 replaces it with wipe-and-reseed).
- Federation, which stays future state.

## Acceptance criteria

- **AC1** Backend builds with 0 errors and **no new warnings** against the
  23-warning baseline, and the full unit suite matches its pre-rename result
  exactly (2431 passed / 1 skipped / 0 failed).
- **AC2** SDK typechecks clean and its vitest suite passes; the frontend
  typechecks, builds, and passes vitest.
- **AC3** Zero residual old-noun tokens in source, excluding the `request`
  family and the third-party `ui/avatar.tsx` primitive.
- **AC4** The transform is **idempotent** — a second `verify` run reports zero
  files left to change.
- **AC5** Every file the sweep would touch is claimed by exactly one partition;
  no file is silently missed.
- **AC6** Strands 2 and 3 are resolved as prose rewrites, not token swaps.
- **AC7** Both consumer repos build and test green against renamed AZOA, and
  both copies of the integration contract remain byte-identical.
- **AC8** The live deployment is reconciled per D29/D31, and the two API keys
  are re-issued (their plaintext is unrecoverable — see the runbook's re-seed
  manifest).

## Non-goals worth stating

The rename does **not** attempt to fix pre-existing defects it uncovers. Two
were found and fixed anyway because they blocked a gate; they are recorded
separately in the runbook rather than being smuggled in as rename work.
