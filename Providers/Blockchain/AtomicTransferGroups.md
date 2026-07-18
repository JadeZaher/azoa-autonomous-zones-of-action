# Atomic transfer groups

`IAtomicTransferGroupModule` is the sole future provider seam for a fee-bearing
primary transfer plus treasury transfer that must be accepted together. Its
request carries two same-asset, same-source, same-`SigningContext` effects and
a stable group identity derived from the resolved provider's canonical
chain/network binding, an idempotency-key digest,
both effects, and signer authority. It never carries a private key or mnemonic.
An implementation may return transaction ids only through one accepted group
result bound to the originating request. The result includes the chain-native
group id assigned to both legs, which is required for the durable receipt. It
must never emulate the contract with sequential transfers. Callers resolve the requested chain/network through
`IBlockchainProviderFactory` before creating a request, so unknown chains fail at
the provider boundary and casing aliases hash as the provider's canonical name.

`AlgorandProvider` assigns the SDK's `TxGroup` id after both typed ASA transfer
legs share suggested parameters, requires their sender to equal the resolved
signing-key address, signs both legs within one custody resolution, and submits
the concatenated signed envelopes through exactly one Algod POST. Rekeyed and
multisig senders are explicitly unsupported and fail closed rather than producing
an envelope the chain would reject.
The accepted result always carries its SDK-assigned group id and both independently derivable transaction ids.
Both confirmed observations are required for `Confirmed`; a timeout or one-leg
observation is `PendingConfirmation` and must be reconciled, never collapsed to
a terminal partial success. Only an Algod 404 means an unseen/pending leg; other
HTTP failures surface as errors. A failed custody/consent check occurs before the
batch POST. This is an adapter capability only: no fee consumer, worker, or
dependency-injection activation may use it until the durable settlement claim
and group-level reconciliation workflow are wired and verified.
