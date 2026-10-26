# Controller integration fixtures

## user-delete-readiness

Self-deletion tests drive the real wallet identity migration to Ready after
schema setup, rather than depending on the background worker's next page. Keep
the owner credential, successful deletion and subsequent not-found assertions;
the production migration and ownership guards remain active.

## NFT ownership and mutation contracts

`UserNFTControllerIntegrationTests` separates ten ownership/binding/read
arrangements from three durable mutation flows. Read arrangements persist a
historical mint journal, matching asset authority and ownership projection with
typed writers/stores. The module edge independently records the exact asset's
owner. These arrangements exercise current ownership checks; they do not claim
to exercise a fresh mint.

The composite-read and foreign-wallet binding arrangements also persist their
historical External wallet through `IWalletStore`, checking the stored identity
and owner before the NFT HTTP call. Wallet creation requires `FirstPartyLogin`,
which explicitly uses real Bearer authentication. Synthetic read/IDOR principals
must not call that protected wallet mutation merely to arrange stored history,
and the fixture must not forward Bearer to TestAuth to make it succeed. The
foreign-wallet case still sends the attacker NFT bind request and requires the
original different-owner refusal; the composite case still requires both actual
bindings. Real JWT/KYC/fee/mint/transfer/soulbound flows remain unchanged.

Mutation flows register and log in real users, validate production JWTs including
expiry, complete first-party KYC through the fake Identomat endpoints, and store
owned platform wallets. Native-fee quotes have a nonzero amount and require exact
acceptance; mint protocol-cost receipts also pass through real quote storage.
Mint/transfer responses are `NftLifecycleView` envelopes and may require explicit
participant acceptance and refresh before completion. Replay assertions retain
the original lifecycle and count distinct module effects. Soulbound refusal
requires unchanged authority and no additional submission.

`NftFlowWebApplicationFactory` preserves the HTTP vendor-edge factory and real
application services. Bearer requests select the real JWT handler; synthetic
principals remain only for the read/IDOR arrangements. Its provider decorator
substitutes the Algorand/Devnet NFT module edge while preserving other providers.
It does not add NFT support to the production Simulated provider.

`NftVendorEdge` has no network or signer dependency. Preparation returns stable
fixture bytes without an effect. Submission reads the real CBOR journal and
requires `Submitted` with the exact persisted preparation before changing its
asset state. Transaction observations refer to recorded submissions; ownership
reads use `(protocol, contract, asset)` state rather than echoing a requested
owner. A new recipient must explicitly accept an asset before a transfer can be
prepared. Create, acceptance and delivery remain distinct for soulbound mints.

This fixture proves controller and durable-store behavior with a local provider
edge. It does not establish native blockchain protocol acceptance.
