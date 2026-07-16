# Atomic transfer groups

`IAtomicTransferGroupModule` is the sole future provider seam for a fee-bearing
primary transfer plus treasury transfer that must be accepted together. Its
request carries two same-asset, same-source, same-`SigningContext` effects and
a stable group identity derived from the resolved provider's canonical
chain/network binding, an idempotency-key digest,
both effects, and signer authority. It never carries a private key or mnemonic.
An implementation may return transaction ids only through one accepted group
result bound to the originating request; it must never emulate the contract with
sequential transfers. Callers resolve the requested chain/network through
`IBlockchainProviderFactory` before creating a request, so unknown chains fail at
the provider boundary and casing aliases hash as the provider's canonical name.

`AlgorandProvider` currently exposes this module with
`SupportsAtomicTransferGroups = false` and fails closed without touching HTTP,
custody, or signing. Its current pipeline can only encode, sign, POST, and
confirm one transaction. Enabling this requires reviewed group-id assignment
compatible with the installed SDK, canonical signing for both grouped
transactions, one batch-broadcast boundary, and group-level reconciliation that
observes both transaction ids. No fee consumer may call it until all four exist.
