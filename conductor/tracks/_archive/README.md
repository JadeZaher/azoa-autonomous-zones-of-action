# Archived Tracks

Tracks here are no longer part of the live catalog (conductor/tracks.md).
Most were retired without shipping and remain in-tree so the design
rationale survives; a few (see "Archived as shipped" below) shipped and
were archived as bookkeeping once their work landed.

## 2026-07-05 — Archived as shipped (bookkeeping)

These tracks are **code-complete and shipped**; they were moved here once
their work (and, where applicable, the owed review) landed. Their catalog
one-liners live in the **Shipped** table of `conductor/tracks.md`.

- **user-sovereign-identity/** — self-owned avatars + external-wallet
  (challenge-signature) auth; tenant-provision-and-lock dropped. Shipped;
  security review done + remediated in commit `10e5dad` (2026-06-22).
- **tenant-consent-delegation/** — user-granted, revocable consent grants
  gating any tenant-triggered value/signing action; single custody
  chokepoint (`KeyCustodyService`). Shipped; same review closeout
  (`10e5dad`, 2026-06-22). See [[consent-gate-architecture]].

## 2026-06-10 — surrealql-toolkit family

The public-toolkit framing ("Prisma for SurrealQL") was abandoned per
RUNBOOK §1 (decided 2026-05-27). The AZOA-internal needs are met by
the existing `Azoa.SurrealDb.Client/Schema/Analyzer` packages plus the
C#-first attribute-driven schema authoring that landed 2026-06-03. The
five tracks below were never started and are kept here as a record.

- surrealql-toolkit/ — umbrella + DESIGN-mermaid-portfolio.md (historical)
- surrealql-drift-detection/ — `azoa-surreal drift` design
- surrealql-db-pull/ — `azoa-surreal db pull` design
- surrealql-studio/ — `/studio` UI design
- surrealql-toolkit-packaging/ — public NuGet packaging design

`data-backfill-migrations/` remains valid as a standalone track and is
NOT archived.
