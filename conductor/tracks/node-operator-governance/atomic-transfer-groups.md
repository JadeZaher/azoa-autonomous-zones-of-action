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

`AlgorandProvider` currently exposes the module as unavailable and makes no
HTTP, custody, signing, or chain call. The installed/used SDK path only proves a
single-transaction build/sign/POST/confirm pipeline. A real adapter requires
verified group-id assignment, canonical two-transaction group signing, one
batch broadcast, and group-level reconciliation. This is a type-safe activation
prerequisite, not execution evidence; every nonzero unsettled consumer remains
fail-closed.
