---
type: review
---

# Shared Solana version-zero source review

Root independently reviewed `SolanaMessageEnvelope`, `SolanaLookupTableSnapshot`,
`SolanaProvider.LookupTables` and `SolanaTransactionSigner` on 2026-10-15 and
accepted the bounded framing/remapping/signature slice. The review verified that
all lookup-table writable addresses precede all readonly addresses, static signer
and program roles remain preserved, and saved/current resolution rejects account
collisions or changed used entries. No runtime check was executed.

The review requested extraction of the lookup reader's WTT-named account parser.
It is now the shared `SolanaAccountData.ReadOwned` helper; DEX does not depend on a
WTT-specific account reader.

This verdict does not approve the WTT source builder, operation first-send/body
claims, or economic settlement. Independent SDK vectors and actual local runtime
proof of version-zero/lookup-table and guarded packet execution remain mandatory
final-sweep gates. Authored structural and detached-signature fixtures are not
reported as passed.
