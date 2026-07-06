---
type: note
track: final-hardening-cutover
title: Phase G doc + bookkeeping closeout
created: 2026-07-05
---

# final-hardening-cutover — Phase G closeout (doc + bookkeeping)

Phase G makes the catalog TRUE after the whole track shipped. Summary of what
Phase G did.

## G1 — Two already-done tracks marked shipped

- **user-sovereign-identity** and **tenant-consent-delegation**: ACs flipped
  (`tenant-consent-delegation` AC11 `[~]`→`[x]`; `user-sovereign-identity`
  SECURITY-REVIEW section marked ✅ DONE), citing the security review done +
  remediated in commit `10e5dad` (2026-06-22, [[consent-gate-architecture]]).
- OKF frontmatter (`status: shipped`) added to both specs.
- Track dirs already physically under `conductor/tracks/_archive/`; the archive
  `README.md` gained an **"Archived as shipped (bookkeeping)"** section so the
  archive no longer implies "retired without shipping."
- Both moved to the **Shipped** table in `tracks.md` (out of "Archived without
  absorption").

## G2 — Operator/deploy tasks folded into docs/NODE-HOST.md

NODE-HOST §8 already covered the DEPLOY-STEPS operator items (secrets, KMS
custody, mainnet gate, fee-funding, KYC, tenant onboarding, Guardian sets +
Railway deploy, brand CI guard). Phase G **added the new residuals** in generic
operator voice:

- **§4.1 (new)** — value-route reality: **Algorand real; Solana/Wormhole/Ethereum
  fail-closed and must stay disabled** (`RealValueEnabled=false`) until follow-ups.
  Includes the **D1-L2 forward-compat residual**: if quests ever become
  shareable/public, value-node actor derivation must switch `quest.AvatarId` →
  `run.AvatarId`.
- **§8.2** — key-rotation **pending-key marker** must live on a **mounted volume**
  with restrictive perms on ephemeral containers.
- **§8.3** — mainnet gate now explicitly requires the target chain to have a real
  value route (cross-links §4.1).
- **§8.9 (new)** — configure the JWT issuer to mint the **`operator:admin`** scope
  (API-key-forbidden); interim legacy `role=Admin` claim works safely.

## G3 — DEPLOY-STEPS-TODO.md retired

Replaced its body with a **retired stub**: a one-paragraph pointer to NODE-HOST §8
plus a final-disposition status board (every code item CLOSED, every operator item
MOVED, deferred follow-ups listed). Kept in-tree (not deleted) because ~26 source
comments and archived specs still cite it by name — the stub keeps those references
resolving to a truthful record.

## G4 — tracks.md reconciled

- **In flight** section now reads **"None"** — no active/pending engineering tracks.
- `final-hardening-cutover` marked shipped (`status: shipped` frontmatter) and moved
  to **Shipped** with a Phase A–G one-line summary + honest deferred follow-ups
  (Solana/Wormhole/ETH value routes disabled; `RewrapAsync` string residual;
  `operator:admin` issuer wiring).
- Shipped count 28 → **32** (+final-hardening-cutover, +user-sovereign-identity,
  +tenant-consent-delegation, +frontend-demo-harness).

## G5 — frontend-demo-harness light audit (non-blocking)

**Dashboard pages present** (`frontend/src/app/(dashboard)/`, 16 feature + 1 tests):
overview, avatars, avatar-nfts, wallets, holons, nfts, quests, blockchain, bridge,
swap, fungible-mint, star-odk, search, api-keys, settings, **tests** (test-runner).

**Shipped-this-track features vs pages:**

- **Ecosystem tree (D2)** — ✅ **has a UI**. `components/ecosystem-tree/`
  (`ecosystem-tree-flow.tsx` + `ecosystem-node.tsx`) is rendered inside the
  **star-odk** page (`EcosystemSection`, GET `/api/starodk/{id}/ecosystem`, plus an
  attach form → `/dapp-series`). No gap.
- **Fractionalization Bridge/Back nodes (D1)** — covered by the existing
  **quests** builder palette (`components/quest-builder/`, `presets.ts` +
  `node-catalog.ts`). No dedicated page needed.
- **Saga operator / dead-letter surface (F1)** — ❌ **no dedicated UI page**.
  Reachable via API/endpoints today. Small; **post-launch** UI item.
- **Key-rotation admin (B5)** — ❌ **no dedicated UI page**. The rotation
  orchestration + admin endpoint exist server-side; no admin console view. Small;
  **post-launch** UI item.

**Verdict:** harness is launch-ready. The two gaps (saga-operator/dead-letter and
key-rotation admin views) are small operator/admin consoles over already-shipped
endpoints — noted for post-launch, **not built** here and **not launch-blocking**.
