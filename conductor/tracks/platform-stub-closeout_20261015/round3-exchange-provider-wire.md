---
type: contract-amendment
status: accepted-design
owner: lane-B
---

# Durable treasury exchange wire

Root independently accepted this contract before implementation. Source and runtime acceptance remain separate gates.

This completes the existing Algorand family; no chain family, endpoint or signing
key source is added. The generic exchange coordinator resolves a provider-owned
IExchangeTransactionModule. Its immutable intent contains physical network and
protocol, exact source ID/generation, canonical decimal amounts, ordered transfer
legs, explicit signer role/context and the reserve fee payer's accepted maximum
network cost. Project treasury roles retain the exact tenant/project reference;
they never fall back to a platform reserve key. SDK exchange clients receive the
same disclosure and durable progress; server custody is not a browser capability.

The Algorand module reconstructs only ordinary ASA transfers for the original
ordered legs. A reserve zero-value self-payment pays the entire pooled group fee,
and every transfer fee is zero. This fixed extra member makes fee-payer placement
independent of whether a units leg is present. Each member carries the same
canonical intent hash note, physical genesis and bounded validity interval.
No rekey, close, clawback, logic signature or multisig is permitted. UInt64
conversion occurs only here, after canonical generic decimal validation.

Preparation uses the existing NativePreparation journal: deterministic
source/generation/domain key, canonical unsigned complete group persisted before
any custody call, then every signer may sign only the winning bytes. The module
verifies canonical per-envelope framing, exact unsigned bytes, each expected
single-sender Ed25519 signature and recomputed group identity. The complete
signed candidate is persisted once. The outer source separately records that
candidate and atomically authorizes first egress under its original two-party
authority. The provider journal never authorizes broadcast itself.

Submission revalidates the stored candidate and physical network, concatenates
only the exact canonical envelopes, and sends no new nonce/rounds/group. An
ambiguous response preserves the same candidate. Already-authorized identical
wire may rebroadcast while valid; new generations require explicit recovery
after authoritative no effect. No local in-memory submitted-key set is safety
evidence. Expired bytes cannot broadcast.

Confirmation requires the entire exact outer group in one finalized indexed
block, in order, with original sender/receiver/asset/amount/fee/note/genesis,
matching positive round, and absence of extra effects. Mutable balances or a
single pending-node receipt cannot settle the exchange. No effect requires
exhausted transaction-ID searches for every member, an indexer caught up beyond
the full validity window, and immutable same-genesis block evidence. Unknown or
partial history preserves commitment and original wire.

Required fixtures: concurrent preparation uses one original unsigned plan;
crash before send and lost response reuse identical candidate; either tenant key
or participant grant revoked before first egress forbids send; trailing envelopes,
different group member, hidden clawback/rekey/close and fee excess refuse; partial
indexed group cannot confirm; incomplete absence cannot refund; confirmed source
accounting survives later authorization expiry. These are authored before the
single final integrated sweep. No new provider capability is advertised before
source review and the existing activation gates.

Protocol/SDK evidence reused: pinned Algorand Transaction/SignedTransaction and
Encoder captures in .omc/research, round3-algod-full-openapi.json,
round3-indexer-openapi-full-2026-10-15.json, and the already-reviewed
AlgorandSignedEnvelope and NativePreparationJournal implementations. Existing
provider group helpers use the pinned TxGroup.AssignGroupID operation; no custom
group hashing or signature algorithm is introduced.
