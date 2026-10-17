---
type: implementation-plan
---
# Round 1 Solana concrete NFT and prepared-value amendment

Design for independent review before source changes. Complete the existing Solana
family through INftTransactionModule and IValueTransferTransactionModule; do not
create another provider registration. Existing server custody remains the key owner.

## Chain identity and exact transaction recovery

Solana provider validates configured immutable network against getGenesisHash.
Prepared transaction identity is the actual first Ed25519 signature over canonical
Solana Message bytes. Persist full signed wire bytes BEFORE submission, plus opaque
provider recovery metadata: genesis, recent blockhash, preparation context slot and
lastValidBlockHeight. Submit revalidates signature, message instructions, economic
intent, genesis and validity; only those exact persisted bytes can be sent again.

Use finalized blockhash/status evidence. A missing transaction/history result does
not prove failure or expiry; never rebuild from absence. Expiry only follows explicit
validity/finalized-height evidence; observation still distinguishes an expired-but-
unknown historical outcome from a proven rejection. Durable nonce accounts require
creation, authority and serialization lifecycle; do not silently infer nonce support
from a recent-blockhash implementation. Provider metadata enables bounded recovery
without adding Solana-specific parsing to generic services.

## NFT and metadata

Derive mint seed from durable PreparationKey; never a fresh GUID on retry. Create a
single-supply zero-decimal mint, metadata URI/name and immutable provenance in actual
chain instructions. Use canonical Metaplex metadata/master-edition account derivation
and parse on-chain mint/metadata ownership before treating it as an NFT. Transfer and
burn use validated mint and owner token accounts; account absence is not zero balance
unless RPC proves absence. Create destination ATA atomically when appropriate and
verify token-program ownership, mint, owner, decimals, supply and frozen/delegate state.

Existing cached Solana.Metaplex 8.0.0 depends on obsolete Solnet.*6.1 whereas app uses
Solana.*8.7 with overlapping namespaces. Avoid a blind package reference. Prefer
compatible maintained builder/source; if narrow official generated codec is required,
record upstream commit/license and pin instruction/PDA/data golden vectors. No custom
curve/signature/base58/transaction framing implementation.

## Logical issuer, payer and atomic fees

Normal NFT operations are holder-authorized. Frozen credentials must preserve issuer
identity in immutable lifecycle authority and require that logical issuer's explicit
operation authorization; platform freeze/delegate authority alone is insufficient.
Recipient opt-in/approval is a separate explicit stage. Frozen classic SPL credential
choreography must retain nontransferability and authorized revocation, including
thaw/delegate transfer/refreeze and exact recorded issuer checks.

Put final NFT/value effect and native fee payment in ONE Solana transaction. No
separate compensatable fee transfer. Fee policy quote/digest/network/payer survive
all retries; fee appears once on final effect, never intermediate acceptance/return.
For platform credential authority plus user-paid fee, a transaction has multiple
required signers. Current signer only returns a fully verified single-signer wire
transaction: add a separately documented provider-owned partial-signature capability
through the custody/factory seam, then assemble with vetted Solnet APIs and verify
all signatures. Never replace payer with platform merely to avoid multiple signatures.

## Evidence and implementation boundaries

Provider-owned partial files for preparation, exact-wire validation, observation,
metadata/ATA decoding and value transfer; mirrored SDK capability/parity changes.
Generic additions restricted to opaque recovery metadata and signing capability
contracts. Root/core author coordinate shared DTOs. No source implementation starts
until this amendment is reviewed.

Tests authored for deterministic preparation, real transaction ID, wrong genesis,
mutated bytes/fee/recipient, expired blockhash vs unknown history, absent ATA creation,
wrong mint/delegate/freeze authority, single-supply metadata, issuer/multiple-signature
requirements and fee once across retries. Execute only in final integrated sweep.
Live devnet evidence remains required before advertising completion.

## Research

Official Solana confirmation/expiry and Metaplex mint docs captured in
.omc/research/solana-nft-contracts-2026-10-15.md; package provenance/dependency evidence
in .omc/research/solana-metaplex-package-2026-10-15.md and local NuGet nuspec. The Solana
skill and provider/signing/interface/conformance AGENTS were read before design.

## Independent review corrections

The deterministic seed is specifically SystemProgram.CreateAccountWithSeed(base,
seed, TokenProgramId), authorized by the protected base-account signature. It is a
public account-address derivation input, never fed to an Ed25519 Account/Wallet secret
constructor. No mint private key exists in this scheme. Domain separation includes
network, actor/base address, lifecycle/stage and preparation generation.

Frozen classic SPL credential choreography requires an empty-holder-account lock:
recipient Accept atomically creates ATA if absent, approves the immutable logical
issuer as delegate for one unit, and freezes that empty ATA using platform freeze
authority. Both recipient and platform sign. Delivery atomically thaws, transfers
one unit, and refreezes; no thawed intertransaction window permits undelegation.
Revoke atomically thaws the holder, transfers as recorded issuer delegate, and
refreezes the emptied source. Issuer + platform signatures are mandatory even with
zero platform fee. Each partial signature is checked against exact canonical message
and declared public key; required signers/instruction count and identities are an
allowlist, not arbitrary extras. Metadata must retain logical issuer binding.

Important dependency to resolve before implementation: standard Metaplex master
edition takes over mint/freeze authority, so credential frozen-account choreography
cannot blindly reuse a master-edition mint. Choose an explicit protocol-compatible
credential standard (single-supply SPL + immutable Metaplex metadata, no transferable
master edition) and prove supply authority removal plus issuer/freeze constraints;
normal transferable NFTs use the correct standard separately. SDK must report the
actual standard, not invent nonfungibility from display metadata.

Expired unknown-history outcomes remain NeedsOperator unless exact finalized block
range coverage establishes the original transaction's absence. Do not rebuild on
blockhash invalidity alone. Local validator and official instruction fixtures provide
actual protocol execution evidence; live devnet is separately reported and does not
prevent local source completion when faucet/network availability is external.

## Accepted implementation constraints

Normal transferable NFTs use Metaplex master editions. Frozen credentials use
single-supply zero-decimal SPL tokens with immutable Metaplex metadata and removed
mint authority; their contextual standard must be carried truthfully through prepared
evidence and committed authority rather than a provider-wide display constant.
Official SPL token processor rejects delegate approval/revocation while frozen;
execution fixtures must prove that control. Delivery revalidates the exact ATA's
owner, mint, frozen state, delegate and delegated amount: closing/recreating the
empty ATA after acceptance cannot bypass consent or freeze policy. Rejection is
safe and requires a fresh authorized acceptance, never silent thaw-and-deliver.
