---
type: design
track: node-operator-governance
status: in_progress
created: 2026-07-13
---

# Atomic-chain fee settlement capability

`IAtomicTransferGroupModule` defines the only acceptable provider contract for
a fee-bearing primary plus treasury transfer on a chain with atomic groups. Its
request is created only from a resolved provider binding, canonically binding
that provider's exact chain/network name, an idempotency-key digest, both
same-asset/same-source effects, and their shared signer authority; it carries
no mnemonic or private key. The result cannot represent an accepted primary leg
without its treasury leg, is bound to the originating request identity, and
rejects duplicate transaction identifiers; adapters are forbidden from sequential
fallback. Unknown chains fail at provider resolution and a mismatched chain or
network fails before a group identity is hashed.

`AlgorandProvider` implements this provider primitive: it assigns one SDK
`TxGroup` id to two typed ASA transfers, requires their source to match the
resolved signing key (rekey and multisig are unsupported), signs both legs in
one custody callback, and sends their envelopes through one batch POST. It
returns both transaction ids and treats incomplete confirmation observation as
non-terminal pending state.

This is not consumer activation. No fee consumer, dependency-injection path, or
recovery worker invokes the primitive yet; the durable settlement lifecycle and
group-level reconciliation activation remain required. Every nonzero unsettled
consumer, including Transfer, therefore remains fail-closed.
