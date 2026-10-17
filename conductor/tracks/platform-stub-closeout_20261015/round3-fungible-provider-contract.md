---
type: contract-amendment
status: accepted-for-bounded-implementation
owner: lane-B
---

# B1 durable fungible creation provider contract

Root independently accepted this wire contract on 2026-10-15. Source review and
the final integrated runtime sweep remain separate requirements.

Classification: complete the existing Algorand family. The advertised manager
create capability is ASA-only, not ERC20/SPL issuance. Generic orchestration selects
a capability without chain switches. SDK mirrors operation/quote/result with decimal
text supply, asset identifiers, fees and minimum balance. Browser signing is N/A for
this server custody endpoint.

## Reuse and ownership

Reuse `AlgorandProvider.BuildAssetParams`, `ApplySuggestedParams`,
`SignViaCustodyAsync` and `AlgorandSignedEnvelope.DecodeCanonical`. The latter rejects
trailing/noncanonical MessagePack; never decode persisted wire with the permissive
SDK decoder alone. Reuse `INativePreparationStore`'s immutable pre-custody plan and
single saved signed candidate under a domain-separated fungible preparation key.
Do not call NFT preparation or inherit its amount-one semantics. Preparation storage
is inert: only outer lifecycle Submitted authorization allows provider submit.

B owns new `IFungibleCreationModule`, `Models/Blockchain/FungibleCreationIntent`,
`FungibleCreationQuote`, `PreparedFungibleCreation`, `FungibleCreationObservation`;
`IFungibleCreationService`, `IFungibleCreationStore`; POCOs `FungibleCreationOperation`
and `FungibleAssetAuthority`; `AlgorandProvider.Fungible.cs`; lifecycle service/store,
manager/controller/action adapters and tests. Stores use the established Surreal
provider directory. Public projections exclude signed wire and custody material.
Root owns Program registrations and shared schema/index composition.

## Quote and durable preparation

The module exposes quote, prepare, exact submit and observe. Intent binds logical
operation/generation/preparation key, actor/issuer, creator and exact signing context,
chain/network/protocol, supply, decimals, name/unit/URL/hash/default-frozen, all four
control addresses, canonical intent hash and accepted quote. The note commits this
immutable intent. Generation replacement requires authoritative previous no-effect
and fresh caller authority.

Quote resolves Algod genesis, bounded encoded transaction fee and creator minimum
balance increment. It binds the entire intent, signer, network, expiry and maximum
accepted network fee/minimum balance. User authorization sees these native costs
before signing; existing requests cannot silently accept unknown costs. Do not infer
a new platform fungible fee from NFT policy.

Validate UTF-8 lengths/protocol limits, canonical positive uint64 supply, decimals
0..19, hash absent or exactly 32 bytes, creator/control address encodings and exact
wallet/platform signing role. The operation is one `acfg`, AssetIndex absent, no
group/rekey/close. Fetch suggested params for the winning plan, pin genesis, validity
window, fee and intent note. Claim the plan before custody: concurrent/restarted
preparers sign the same winning unsigned bytes. Expired winning plans cannot fetch
new rounds under the same generation. Custody output must be canonical, exactly
match the saved unsigned transaction and prove the expected signer before save.

Submit verifies stored journal identity, canonical wire, tx ID, complete intent/quote
and bound network. Send only those saved bytes. Repeated submit is permitted for
outer Submitted authorization and the same identity while valid; no provider worker
may discover and send a Prepared row. Expired wire is observed rather than replaced.

## Authoritative creation evidence

Use configured Indexer receipts, not latest mutable asset state. Require exact tx ID,
`acfg`, creator, validity window, intent note, fee, creation rather than update/destroy,
all original parameters and positive `created-asset-index`. Normalize optional
control addresses only through provider-defined absent/zero encoding. Receipt round
must be within the signed window, indexed round must cover it and the corresponding
block genesis must match saved protocol identity. Reject extra candidates,
continuation tokens and incomplete semantics. The finalized receipt supplies AssetId;
TxId never occupies that field. Pool errors/pending responses cannot prove absence.
Later control-address changes do not rewrite original issuance evidence.

ExpiredAbsent requires current node past LastValid, exhausted exact tx search,
indexer caught up through LastValid and matching genesis block evidence. Unknown,
unavailable, pruned or partial history retains original bytes and NeedsOperator.
Operator-attached evidence passes the same verifier against frozen intent, never
accepting an arbitrary asset ID. Proven no-effect allows cancellation or an authorized
new generation without erasing history.

## Projection and public flow

Existing `RegisterAssetAsync` is not an atomic-plan seam. Extract a typed reserve
registration plan and its accounting/uniqueness guards; compose it with asset
authority, optional Holon witnesses/link and operation completion in one transaction.
No fallback to independent writes. Persist network/protocol in reserve identity.
Local conflict after chain success stays ProjectionPending for the same asset.

Direct journey: bound quote, explicit quote authorization plus original request key,
operation receipt, authenticated observation/continue and participant discovery.
Action recovery can admit without signing and park for actual participant continuation;
it cannot synthesize quote consent/new generation signing. Recovery may atomically
project known final evidence and rebroadcast exact previously authorized bytes.

## Evidence and acceptance

Current official sources captured in `.omc/research/round3-asa-official-2026-10-15.md`
and its readable extraction: https://dev.algorand.co/concepts/assets/asset-operations/
and https://dev.algorand.co/reference/rest-api/indexer/. Pinned SDK wire-type sources
are the existing `algorand-pinned-AssetConfigurationTransaction.cs.md` and
`algorand-pinned-Encoder.cs.md`. Capture targeted Indexer schema and fee/minimum
balance references before implementing those calculations/parsers.

Author max uint64/UTF-8/absent authority, quote mutation/expiry/budget, pre-custody
plan contention, trailing-object, wrong signer/genesis/params/index and partial-history
fixtures. Persist-before-send and lost-response recovery must yield one asset;
finality-before-projection crash must yield one atomic authority/reserve/Holon result.
Real local Algorand+Indexer and Surreal race gates remain required in the final sweep.
No build/test/schema/protocol runs occurred for this contract.

## Provider source review

Root independently accepted the bounded provider slice after reviewing accepted-admission quote expiry, universal signing scope, canonical metadata/genesis and malformed evidence guards. This verdict covers provider source only. Lifecycle/API/client integration and authored fixtures remain under construction; no build, tests, schema or protocol validation has run.
