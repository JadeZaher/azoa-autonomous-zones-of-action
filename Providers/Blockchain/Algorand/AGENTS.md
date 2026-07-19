# Algorand provider notes

## Bridge capability

`AlgorandProvider.SupportsBridging` is deliberately false. The provider can
broadcast a platform-signed ASA transfer into a configured vault, but that
partial primitive is not an executable bridge lifecycle.

Wrapped mint, wrapped burn, and source release fail closed. The retired mint
path created a new ASA with the recipient as its control addresses and did not
implement a canonical platform-controlled ASA, recipient opt-in, then transfer.
Consequently its paired destroy operation could not prove the platform owned the
full supply. Source release is also refused until the configured vault is proven
to be controlled by the same signer used for the release transaction.

Do not set `SupportsBridging=true` until all four primitives have focused tests
for confirmed submission, pending-confirmation recovery, ownership/opt-in, and
failure without fabricated success.
