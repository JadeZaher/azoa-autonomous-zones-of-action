---
type: implementation-handoff
---
# Round 1 Solana durable provider handoff

Authored and independently source-reviewed; not compiled or executed. Review record:
`round1-solana-source-review.md`. The reviewer found a public-key/consent-context
collision; the final builder now rejects conflicting contexts before signing and
rechecks on recovery. A two-case adversarial test asserts no custody calls.

## Files

Provider: `Providers/Blockchain/Solana/SolanaTransactionSigner.cs`,
`SolanaProvider.cs`, `SolanaProvider.Durable.cs`, `SolanaProvider.ValueTransfer.cs`,
`SolanaProvider.Nft.cs`, `SolanaProvider.NftInstructions.cs`, `SolanaNftMetadata.cs`,
`AGENTS.md`. Generic seam: `Interfaces/Signing/IPartialTransactionSigner.cs`,
`Models/Blockchain/PreparedValueTransfer.cs`, `Interfaces/AGENTS.md`,
`Services/Signing/AGENTS.md`. Core NFT author owns the coordinated trailing prepared
RecoveryMetadata/TokenStandard fields and their lifecycle persistence.

Tests: unit `Signing/SolanaDurableLifecycleTests.cs`,
`Signing/SolanaSignerVerifyFailPathTests.cs`, `TestSupport/SolanaDurableFixture.cs`;
integration `SolanaNftProtocolTests.cs` and `Fixtures/Solana/{Containerfile,AGENTS.md}`.
SDK: `src/solana/AGENTS.md`, `api/client.ts` credential revoke historical network and
contract fields. The shared lifecycle client is the custodial NFT parity surface.

## Invariants and authored evidence

- Canonical detached signatures require the actual declared required key; complete
  envelope must contain exactly the expected participant set. No custody fallback.
- Actual signature and exact signed bytes are persisted before send. Prepared value
  generation and complete NFT/fee authorization are signed in Memo intent digest.
- Finalized observation verifies exact wire history and bound genesis. Missing or
  expired unknown history remains ambiguous, never a no-effect/replacement proof.
- Normal NFT: supply-one zero decimals, immutable metadata and master edition.
  Credential: supply-one SPL, immutable issuer metadata, removed mint authority,
  recipient-signed delegate approval plus freeze, atomic thaw/transfer/refreeze,
  logical issuer signature for revocation. Actual standard remains explicit.
- Atomic fee instruction appears once on final effect. Intermediate accept/return
  do not charge platform fees. Network gas is not represented as refundable.
- Local protocol tests execute real master edition/fee and frozen SPL approval,
  delegate-revocation refusal, close/recreate ATA race and final ownership. Unit RPC
  fixtures prove orchestration only and are labelled accordingly.

Static API lookup caught mandatory TokenProgram program-id arguments and Solnet's
random ignored key in null SetAuthority. The latter is replaced by official SPL
wire `[6,0,0]` (remove mint authority), pinned by an exact byte assertion. Existing
provider catch-all handlers were narrowed so cancellation/programming exceptions
are no longer silently converted. No raw database calls introduced. Diff whitespace
check reported no errors. No tests/build/lint/typecheck/container build executed.

## Final execution setup and limits

Pinned local image instructions are in integration Fixtures/Solana/AGENTS.md.
Official Agave v3.1.8 release artifact SHA256 is checked at image build; public
Metaplex program is cloned read-only from devnet. All execution/funds are local.
Record cloned program evidence and test results in final sweep. Live devnet value
execution has not been claimed. Official source/layout/license snapshots live in
`.omc/research/`; GitHub commit API access was unavailable, so captured files plus
client manifest version are the protocol-source revision evidence.

Existing context-less standalone metadata/update/close-account contracts, bridge
primitives and direct DEX exchange/swap remain internal work for Round 2 assessment.
The new durable NFT metadata/ATA path is real source, not a claim those older APIs
were completed. No external deferral was created for that work.
