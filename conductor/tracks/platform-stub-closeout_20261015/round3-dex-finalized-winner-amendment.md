---
type: design
status: accepted-for-implementation
---

# Finalized alternate-signature winner

Root independently accepted this amendment on 2026-10-15. Runtime acceptance is
pending the final integrated sweep.

Two valid low-S Ethereum signatures can cover exactly the same unsigned type-2
transaction while producing different transaction hashes. Custody and browser
signing can race, and external wallet software need not use the same nonce for
its ECDSA calculation. A saved candidate hash therefore cannot be the sole
identity of the authorized effect.

Only an authoritative finalized nonce winner may add alternate candidate
evidence. The complete unsigned type-2 payload must be byte-identical: chain ID,
nonce, destination, value, data, gas limit, fee fields and access list. Its full
signed encoding must be canonical, low-S and recover the original owner. The
provider must prove the actual finalized physical-chain winner and exact effect.
A pending candidate, changed payload or caller-supplied hash cannot replace it.

The journal retains the original `PreparedJson` and hash. A terminal observation
appends the recovered candidate, original prepared hash and original plan hash;
the stage/operation version transaction admits it once. Settlement reports the
actual winning transaction IDs and records both original hashes. Reconciliation
cannot overwrite a terminal receipt. Allowance release additionally requires the
actual winner's resource-bound zero-allowance proof.

Required fixtures cover two valid signatures over identical unsigned bytes,
changed chain/nonce/fees/access list/data/owner refusal, pending replacement
refusal, lost observation replay, competing terminal commits, original wire
retention, actual winner settlement IDs and allowance proof binding. No runtime
execution is authorized before the final integrated sweep.
