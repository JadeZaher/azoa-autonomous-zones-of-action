---
type: Implementation Handoff
title: Round 1 NFT core and Algorand lifecycle
status: Authored; separate review and integrated verification pending
---

The former NFT paths fabricated token identifiers or changed database ownership without a chain effect. Both controller families, credential issuance/revocation, and NFT action adapters now use a durable lifecycle. Arbitrary imported rows cannot establish issuer authority or satisfy current-owner access checks.

## Authored core

- Immutable tenant/actor/operation request identity, original issuer and participant wallets, native fee quote and quote-id authorization before signing.
- Exact prepared bytes, transaction/protocol/contract identity, provider recovery metadata and token standard persisted before submission CAS. Exact-byte replay, authoritative no-effect replacement generations, and archived attempt verdicts preserve recovery evidence.
- Active authority-version reservation before asset movement; atomic confirmed lifecycle, canonical unique asset authority and ownership projection commit. Generated typed writers have disjoint parameter scopes inside the transaction.
- Recipient acceptance, creator-authorized destruction after return, issuer revocation and recipient-authorized refunds. Refunds bind the original completed transfer/current authority; a closed original destination requires its own owner's opt-in.
- Participant-scoped discovery and key lookup; cancel-by-key tombstone atomically competes with delayed admission. Prior confirmed stages prevent cancellation, while authoritative no-effect attempts preserve their history and remain cancellable.
- Separate explicit native NFT flat-fee policy. Quote digest binds network, operation, currency, decimals, treasury, payer, revision and amount. Existing-asset/refund quote resolves historical network. Final effect and fee are atomic; intermediates do not charge another platform fee.
- Current-chain ownership on composite/binding/access paths. Explicit boolean denies override named permission levels. Original mint timestamp remains tied to issuance.

## Algorand module

The provider prepares actual ASA create, opt-in, clawback delivery/revoke, ordinary transfer, return and destroy transactions. The signed note commits the immutable intent. Platform credential effects include an actual issuer-signed atomic authorization payment, either zero to self or the final quoted treasury fee. Recipient opt-in never uses platform impersonation. Individual envelopes must exactly re-encode; trailing MessagePack cannot hide an extra transaction. Authoritative expiry absence requires matching genesis and caught-up indexer evidence. Missing proof keeps the original identity recoverable.

## Principal source ownership

Core: `Services/Users/NftLifecycleService*.cs`, `NftNativeFeePolicy.cs`, `UserNFTService.cs`; `Managers/NftManager.cs`, `CredentialManager.cs`; `Interfaces/INftTransactionModule.cs`, `Interfaces/Managers/INftLifecycleService.cs`, `Interfaces/Stores/INftLifecycleStore.cs`; `Models/Blockchain/Nft*.cs`, `Models/Responses/NftLifecycleView.cs`; `Persistence/SurrealDb/Models/NftLifecycle.cs`, `NftAssetAuthority.cs`; `Providers/Stores/Surreal/SurrealNftLifecycleStore.cs`; `Core/Surreal/SurrealGeneratedStatementScope.cs`; NFT helpers and controllers. Existing action adapters now call the lifecycle but still need the action parking package below.

Algorand: `Providers/Blockchain/Algorand/AlgorandProvider.Nft.cs`, `AlgorandSignedEnvelope.cs`, provider capability declaration. SDK/UI and Ethereum/Solana providers are separately owned parallel lanes.

## Authored verification; not executed

- `Users/NftLifecycleSafetyTests`: overlapping preparation, cancellation against delayed admission/preparation, exact-wire crash/lost-response recovery, no-effect identity checks, cancellation/history preservation, fee consent/replay and holder-authorized refund.
- `Users/SurrealGeneratedStatementScopeTests`: real typed writer parameter isolation, quoted text, invalid shape rejection.
- `Users/UserNftBindingAccessTests`: positive read binding and explicit-deny/owner enforcement.
- `Credentials/FrozenSoulboundClawbackTests`: actual Algorand signer/HTTP adapter through create, recipient opt-in, delivery and frozen clawback; issuer signatures, nonzero atomic revoke fee, appended primary/additional envelope rejection.
- Integration `SurrealNftLifecycleStoreTests`: real schema and atomic projection, stale replay, reservation/cancellation, concurrent claims and no-effect audit retention.
- Existing manager/credential/action fixture migrations are owned by the separate SDK/fixture lane.

No build, test, lint or schema-generation pass has run for this batch. `git diff --check` produced no whitespace errors. This is an author handoff, not approval or runtime proof. New POCO fields require generated schema refresh in the integrated sweep.

## Remaining implementation, not external deferral

1. Action workflow parking/resumption must persist the lifecycle reference and reconcile it without marking a pending recipient journey failed or an intermediate transaction successful. The existing Running replay behavior also needs review; see `action-lifecycle-review.md`.
2. Automatic submitted-operation observation/projection worker is absent. Current authenticated refresh is usable, but a closed browser should not strand previously submitted operations. The worker must be restricted to admitted egress and never prepare new signatures or impersonate recipient/creator authorization.
3. Ethereum and Solana provider parity is in separate implementation lanes. Capability absence is a fail-closed boundary, not completion of advertised provider functionality.
4. Schema regeneration, integrated compilation/tests, separate reviewer findings and fixes remain before acceptance. Only external vendor/configuration prerequisites belong in `deferred.md`; none of the above is an external deferral.

Final integration corrections: credential revocation now supports explicit historical network and retains admitted network across retries, including cancellation tombstones. NFT wallet/access/refund checks compare valid Ethereum addresses independently of checksum casing while preserving Solana case, and require returned owner identity as well as asset existence. Focused regressions are authored in CredentialRevokeNetworkTests and ChainAddressIdentityTests; not executed.
