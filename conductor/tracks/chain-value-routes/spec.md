---
type: spec
track: chain-value-routes
created: 2026-07-06
status: pending
horizon: post-launch
depends_on: [final-hardening-cutover]
---

# chain-value-routes — bring the disabled value routes live

## Why

`final-hardening-cutover` shipped an honest posture: Algorand real bridge value
is LIVE; Solana, Wormhole, and Ethereum value routes are **fail-closed and
disabled** (`RealValueEnabled=false`). This track is the consolidated backlog to
bring those routes live and close the remaining custody/signature residuals.
Sources: `_archive/final-hardening-cutover/{CLOSEOUT.md,CLOSEOUT-PHASE-H.md,spec.md}`,
`docs/RESIDUAL-RISK-RUNBOOK.md`, `docs/GO-TO-PROD.md`, `docs/NODE-HOST.md §4.1/§8.3`.

## Scope

1. **Real Solana value pipeline** — SPL transfer construction + real signing on
   the value path (signer + keygen exist; the value route stays disabled until
   the full lock/burn/settle pipeline is real).
2. **Real Wormhole sequence parsing** — parse the emitter sequence from the real
   core-bridge log instead of the current fail-closed stub; live-network VAA
   validation against a provisioned guardian set (`docs/GUARDIAN-SET-SETUP.md`)
   before any Wormhole value flow is enabled.
3. **Real ETH secp256k1 keygen** — replace the fail-closed stub.
4. **`ConnectWalletAsync` signature verification** — verified safe-for-alpha
   (external wallets cannot reach the custody chokepoint) but the address-squat
   vector should close in v1: require a signed challenge proving key possession.
5. **`RewrapAsync` cold-path residual** — custody boundary returns zeroable
   `byte[]`, but the cold path still routes a hex `string` intermediate; align it.
6. **Soulbound clawback-revoke** — mint path shipped and platform holds the
   clawback role; add the revoke operation.
7. **Metaplex dormant methods** — convert-or-delete
   `SolanaProvider.{CreateMetadataAccountAsync,UpdateMetadataAsync}` (no caller).

## Status reconciliation (2026-09-07)

The Scope list above is the ORIGINAL intent and is preserved as written. Two of its
seven items have since closed and one partly closed; `plan.md` carries the live
per-item state with file:line evidence read at commit `b2576f4`. In short:

- **Item 3 (real ETH secp256k1 keygen) is SHIPPED** — `Services/Signing/WalletKeyService.cs:158-170`,
  whose comment cites this track as "chain-value-routes H1".
- **Item 6 (soulbound clawback-revoke) landed but is inert**, and its remaining
  defect now belongs to `credential-third-party-issuance_20260906`.
- **Item 7 (Metaplex)** was converted to fail-closed rather than deleted; the
  implement-or-delete decision is still open.

## Acceptance

- Each route flips `RealValueEnabled=true` only behind its own devnet/testnet
  round-trip drill (mirror the Algorand G-gate evidence pattern).
- Mainnet gate (`NODE-HOST §8.3`) continues to require a real value route for
  the target chain — no route ships enabled-by-default.
