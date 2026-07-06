# Deploy-Steps & Stub-Remediation Registry — RETIRED 2026-07-05

> **This registry is retired.** It existed to track every stub, deferred
> primitive, and operational pre-requisite between the shipped *architecture* and
> production-grade *primitives*. As of the `final-hardening-cutover` track
> (shipped 2026-07-05) that gap is closed: **every code item here has landed**,
> and **every remaining operator/deploy item now lives in the operator guide**,
> [`docs/NODE-HOST.md` §8](../docs/NODE-HOST.md) (going-to-production checklist).
>
> - **Operators:** use **`docs/NODE-HOST.md` §8** — it is the single, generic-voice
>   checklist for secrets, KMS custody, the mainnet gate, fee-funding, KYC, tenant
>   onboarding, Guardian-set provisioning + Railway deploy, the brand-boundary CI
>   guard, and the operator-admin scope. The hard launch gate + sign-off table
>   live in **`GO-TO-PROD.md`**.
> - **This file is kept in-tree as a stub** only because source comments and
>   archived specs still cite it by name; the status board below records the final
>   disposition of every item so those references resolve to a truthful record.

## Final disposition (all items closed or moved)

**Code items — all CLOSED by `final-hardening-cutover`:**

| ID | Item | Final state |
|----|------|-------------|
| B1 | Real Algorand keygen | ✅ done (signing-core-keystone) |
| B2 | Real Algorand signing + real bridge lock/burn | ✅ done — Algorand real; the always-true provider `VerifyBridgeProof` was **deleted** (only the `WormholeAdapter`/`Secp256k1VaaSignatureVerifier` path remains) |
| B4 | Fiat-allocation idempotency | ✅ done |
| B5 | Cross-tenant isolation | ✅ done |
| P1 | Key zeroing (byte[]) | ✅ done at the custody boundary (`DecryptPrivateKeyBytes`). Residual follow-up: the `RewrapAsync` cold-path still routes a hex `string` intermediate |
| P2 | Key rotation | ✅ live orchestration done (dual-key window, batch re-wrap, rollback, admin endpoint) — operator note: persist the pending-key marker on a volume, `NODE-HOST` §8.2 |
| P5 | KYC gating (mint) | ✅ done at the NftManager choke point |
| P7 | Reconcile-before-retry (quest engine) | ✅ done (no double-mint on chain-action nodes) |
| A1 | Durable quests inert under `Sagas:Enabled=false` | ✅ fixed (`Sagas:Enabled=true` default + boot guard) |
| NU1903 | AutoMapper high-severity vuln | ✅ fixed (12.0.1 → 15.1.3) |
| H1 | Solana/Ethereum real keygen+signing | Algorand real; **Solana/ETH value routes fail-closed and stay disabled** until follow-ups — see `NODE-HOST` §4.1 |

**Operator items — all MOVED to `docs/NODE-HOST.md` §8 (generic operator voice):**

| ID | Item | Now in NODE-HOST |
|----|------|------------------|
| B3 | KMS/HSM custody option (swap seam landed) | §8.2 |
| B6 | Mainnet enablement gate | §8.3 |
| P3 | Platform ALGO fee-funding + low-balance alerting | §8.4 |
| P4 | KYC provider secrets + enabling Veriff | §8.5 |
| P5' | Wallet-generate pre-KYC policy decision | §8.5 (note) |
| P6 | First-tenant onboarding execution | §8.6 |
| — | Guardian-set provisioning + Railway v3.1.4 deploy + version sweep | §8.7 (→ `GUARDIAN-SET-SETUP.md`, `RUNBOOK.md` §2) |
| H5 | Brand-leak CI guard | §8.8 |
| — | operator-admin JWT-issuer scope minting | §8.9 |

**Deferred follow-ups (honest residuals, non-blocking for launch):**
- Solana / Wormhole / Ethereum real value routes (fail-closed, disabled) — `NODE-HOST` §4.1.
- `RewrapAsync` cold-path hex-string residual (P1) — the boundary byte[] path is zeroed; the cold path is a follow-on.
- H2 soulbound clawback-revoke — post-launch (mint path shipped; platform already holds the clawback role).
