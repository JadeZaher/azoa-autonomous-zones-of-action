---
type: Directory Notes
title: Services/Users
description: Durable NFT lifecycle, issuer provenance, fee consent, and participant recovery.
tags: [nft, credential, platform-stub-closeout]
---

# NFT lifecycle

`NftLifecycleService` is the single mutation path used by both NFT controllers, legacy managers, credential issuance/revocation, and action adapters. Request identity includes authenticated tenant, actor, operation and key. The immutable request records issuer, participants, wallets, policy, and quote before any signing. Imported token identifiers cannot create issuance authority. Completed chain evidence commits the unique network-aware asset authority and application ownership together; arbitrary legacy ownership rows confer neither issuer nor access authority.

Preparation persists exact signed bytes, transaction identity, protocol identity and provider recovery metadata before the Submitted CAS allows transport. Competing preparations cannot authorize egress from a losing CAS. Recovery can rebroadcast identical bytes under provider replay guarantees. Only an exact transaction/protocol/contract no-effect verdict (`ExpiredAbsent` or finalized `FailedOnChain`) permits a new preparation generation. Every attempt keeps its bytes, verdict and observation time. Confirmed-stage count prevents cancellation after earlier create, opt-in or return effects; proven-no-effect attempts do not prevent cancellation.

Asset reservation atomically checks the live lifecycle and authority version before preparing a movement. It serializes different transfer, burn and revoke keys for one asset. Recipient opt-in and original-creator destruction require that participant's own signing scope and current consent. A transferred Algorand burn first returns the asset, then awaits the original creator's destruction authorization. A refund pins the original completed transfer and its exact current authority version; only its current holder can sign the return. If the original sender closed their holding, that sender must authorize opt-in before the holder resumes the refund.

# Native NFT fees

NFT fees have a separate explicit `NftFees:Policies` schedule: native atomic units per chain/network and operation, revision, treasury. Mint, gift transfer, burn, revoke and refund have no sale consideration, so no percentage fee is implied. Missing policy is unavailable, explicit zero is free, and fungible split-fee schedules are unchanged. Quote identity hashes every term, including payer, network and treasury. Nonzero fees require the caller's exact quote identifier, revision and maximum. Asset/refund quote routes derive the bound historical network. Replays retain the admitted quote after configuration changes.

The original requester pays the platform fee once at the final logical effect, atomically with that effect. Acceptance/return stages do not charge it. Other participants sign only their own required chain stages and bear their network transaction costs. Provider authorization carries original actor/issuer contexts independently of the current stage signer. Zero-fee final effects still carry an explicit immutable quote.

# Recovery and reads

Participant discovery pages over an immutable creation-time snapshot and filters terminal rows after paging; an empty page can have a next cursor. The server filters participants before pagination. Lookup by request key is authoritative for an existing admission; absence cannot disprove an in-flight request. Cancel-by-key atomically competes with admission and writes a permanent cancellation tombstone when it wins. A delayed original request sees Cancelled and cannot sign. Existing submitted operations retain their original state and cannot be cancelled as unsent.

NFT composite/binding reads and access checks require the authenticated current on-chain owner, with active projection. Binding maps use explicit boolean values, including deny precedence over `full`. Named `read`, `write`, and `admin` levels grant only that exact permission; `read-only` grants read. Admin does not implicitly broaden privileges. Bound names and wallet addresses are hydrated from their real stores. Original mint time comes from the issuance journal.

# Remaining implementation wave

The core exposes explicit refresh and restricted submitted-operation convergence. Action-engine parking/reconciliation remains tracked implementation work, not an external deferral. Workers never create new preparation or impersonate participant acceptance merely because a lifecycle is pending. Ethereum and Solana module parity has separate source reviews. All new tests are authored but execution is reserved for the final integrated sweep.

## Submitted NFT convergence

NftLifecycleRecoveryWorker calls the separate INftLifecycleReconciler capability. Only Submitted, NeedsOperator and Confirmed records enter it. A typed CAS rotates UpdatedAt before provider reads so an outage cannot pin the oldest batch. Preparation and Prepared-to-Submitted authorization are forbidden in this path. Confirmed intermediary stages and final no-effect evidence may update the journal, but return before any next-stage or replacement signature. Worker exceptions are isolated per record and cancellation propagates.

### NFT protocol cost selection

Fresh Solana mint loads an explicitly accepted server receipt for the authenticated actor/tenant. Admission persists that selected quote and delegates exact command binding plus database expiry to the lifecycle store. Replay resolves the original lifecycle before reading mutable prices or quote availability. Old unprepared Solana Create and replacement generations without explicit accepted cost consent refuse new preparation; prepared transaction recovery retains its original bytes. Provider policy/cost enforcement and explicit replacement authorization must remain separate from historical quote identity checks.

### Public NFT cost offers

QuoteMintCostsAsync records a five-minute immutable offer for the exact actor, tenant, payer, request key and metadata draft after value access, governance and platform-fee consent checks. Existing admission returns only its original matching receipt; it never silently re-quotes a replacement. Quote and preparation share NftLifecycleIntentFactory, including issuer-funded credential stages. Quotation does not claim a lifecycle, reserve assets or invoke custody. Admission remains the database-time expiry boundary.

Platform-signed credential stages retain PlatformAuthorityAddress in the immutable command. Cost quotation includes that identity through the exact initial intent. Preparation refuses a changed or missing current key address rather than resolving a replacement signer from configuration. Revocation inherits the signer from the exact completed issuance lifecycle and confirms its asset/protocol/contract/creation transaction provenance. Historical rows without that signer evidence require explicit recovery; neither current configuration nor a new issuer declaration supplies it.

### NFT participant acceptance

RefreshAsync accepts participant signatures only with an explicit version/state/stage/generation request and actual captured credential. The immutable stage consent retains a separate participant wallet and tenant without replacing the command actor authority. A lost-response replay resolves the saved request first and can resume only that exact stage/generation. Intent construction takes the participant tenant from saved consent. The browser archives the exact acceptance before HTTP, so retries cannot silently target a newer lifecycle stage. Replacement cost authorization remains independently required.

### NFT replacement creation costs

A Solana Create generation above zero is preparable only with its own entry in ReplacementCostConsentsJson; NftLifecycleIntentFactory selects the original command quote for generation 0 and the current generation's replacement quote otherwise, so no generation inherits another's caps. QuoteReplacementCostsAsync requires the caller-reviewed version/state/stage/generation, the recorded authoritative no-effect predecessor, the original payer wallet, the current captured mint credential, and the pinned platform signer; it records an immutable five-minute offer and never signs. AcceptReplacementCostsAsync replays an exact saved request before mutable reads, refuses a request ID rebound to other costs, and leaves RequestJson untouched. RetireReplacementCostsAsync advances the same version under current authority so a delayed acceptance cannot commit after the browser abandons it. Reconciliation is Applied, then Obsolete (retired or higher version), otherwise Unresolved; elapsed time is never evidence. Acceptance records permission only. The accepting credential and the receipt's database-time expiry are fenced once, at the acceptance commit; the accepted receipt is then immutable evidence. Later preparation and first send recheck the receipt binding (generation, full intent, payer, physical network) and fence the current requester authority (the original witness or its latest renewal), so a lapsed accepting credential cannot stall the generation.

### NFT requester renewal

A direct JWT or API-key requester witness lapses (login JWTs live 24 hours), and every later new-effect transition (participant consent append, preparation, first send) fences requester authority. Without renewal, a recipient, holder or creator answering after expiry could never continue. Renewal does not change cancellation: only unconfirmed, unsent work can be cancelled, so a lifecycle past a confirmed stage still has to finish. RenewActorAuthorityAsync appends a fresh direct authority to ActorContinuationsJson for the exact reviewed version/state/stage/generation. The credential rule follows the reviewed fungible continuation precedent: any live direct credential (JWT or API key) of the same user under the same tenant, bound to the same owned wallet and the operation's signing scope, may renew. The credential kind and API-key ID are not pinned, and a renewal also supersedes a revoked original witness, because the renewing credential itself proves current authority and is fenced live in the renewal transaction. NftActorContinuationIdentity.Effective selects the latest matching renewal (otherwise the original) for later requester fences; a renewal that does not bind the original requester fails closed. RequestJson and the original ActorAuthority never change. Workflow-admitted lifecycles cannot be renewed with a direct credential: their witness has no wall-clock expiry and continues only through the original run.

A renewal-only write fences just the new credential. It skips the replacement-cost and participant-consent checks, because it signs nothing, and those checks run again at the next preparation or first send. This is what lets renewal work while a holder has not yet authorized a refund, after a soulbound replacement Create confirmed, or before replacement costs are accepted. NeedsActorContinuation is a display hint; the transaction fence is authoritative.

### NFT participant renewal

A participant's stage consent credential can lapse between acceptance and preparation or first send. The same participant (same user, tenant and wallet) re-accepts the unchanged stage and generation through the accept route with a fresh request, but only after the held credential has lapsed or been revoked (checked in the store batch), so a live participant cannot repeatedly bump the version and invalidate the requester's in-flight cancel, cost or renewal requests; the store appends it to StageConsentsJson only while the row is Preparing or Prepared, with the identical intent hash and nothing else changed (`NftStageConsentIdentity.CanRenew`). `Current` returns the latest entry for the stage and generation, so the next signing step fences the renewed credential, and a renewal-only write fences only that credential. Fresh consent for a new stage still requires that no entry exists for that stage and generation. NeedsParticipantRenewal and RenewalParticipantUserId in the view are display hints.

### NFT tenant scope

A requester sees, refreshes and cancels only lifecycles admitted under the same tenant context (`RequesterOwns`), mirroring `FungibleCreationService.Matches`; a refund and a refund fee quote must name a source transfer the requester owns under that same tenant. A counterparty (recipient, holder or creator) sees an incoming lifecycle until its first stage consent; afterwards only under the tenant that consent bound. Cancelling admitted work requires the requester's current credential and its operation signing scope, fenced with `SigningAuthorityFence.ForConsent` in the cancellation transaction. Only the exact never-admitted cancellation marker is written without authority.

### NFT owner takeover (F-H12, round 4)

A lifecycle admitted by a tenant (child credential) or by a workflow run cannot be continued once the tenant grant is
revoked or the run stops, and past a confirmed stage it cannot be cancelled either, stranding paid rent. The owner's own
direct credential (no tenant) may renew it through `RenewActorAuthorityAsync` at any continuable state
(`NftActorContinuationIdentity.Continuable`: Preparing, Prepared or awaiting a participant), not only past a confirmed
stage (RV-L5-09 decision: allowed, the asset is the owner's). The renewal is recorded as an owner takeover
(`NftActorContinuationIdentity.IsOwnerTakeover`). From then on `RequesterOwns` admits only the owner outside any tenant,
the tenant can neither see, cancel nor renew back in, the store stops fencing the original workflow run, and later effects
fence the owner's renewal. `RequestJson` and the original intent never change.

Because the run's fence is dropped, the run itself is locked out explicitly (RV-L5-09): a service call carrying the run's
`ActionAuthority` is refused at the `AdvanceAsync` seam once the lifecycle is taken over, and its authority-bearing writes
are passed to the store with `workflowAuthority`, which refuses them against the stored row inside the same version CAS.
A cancelled run's in-flight handler therefore can neither sign nor report success; its node stays pending on the
lifecycle observation.

### NFT recipient-stage costs (F-M11, round 4)

Solana stages whose message can create the destination holding (`Accept`, `Transfer`, `ReturnForBurn`;
`NftStageCostContract.Required`) spend associated-token-account rent and network fees. The fee payer is the accepting
recipient on a non-refund `Accept` and the requester otherwise (`Payer`). That payer, and only that payer, quotes
(`QuoteStageCostsAsync`, route `POST /api/nft-stage-costs/{id}/quote`) and accepts (`AcceptStageCostsAsync`, `/accept`)
an immutable five-minute offer bound to the exact reviewed version/state/stage/generation, lifecycle draft, payer wallet,
physical network and stage intent. The payer acts under its own stage consent's tenant when it signs the stage, otherwise
under the requester's effective tenant (`PayerTenant`), so issuer or tenant authority is never reconstructed as recipient
consent. Acceptance is append-only in `StageCostConsentsJson` (`option<string>`, null on rows that never accepted one);
the store commits it with the payer's credential fence and the receipt's database-time expiry in one batch, and records
permission only.

Without a binding acceptance the stage stays `Preparing`, unsigned, with `NeedsStageCosts` in the view; the store also
refuses preparation and first-send writes (`AdmitsWrite`). Before preparing, the service re-quotes read-only; if current
costs exceed the accepted limits or the physical network or standard moved, it refuses with `NFT_STAGE_COSTS_CHANGED` and
the payer quotes and accepts again (a later acceptance for the same stage and generation supersedes the earlier one, only
while no wire exists). The accepted receipt rides in the intent (`ProtocolCostQuote`), and the Solana provider enforces
it before custody and again before send. The stage consent hash excludes the cost receipt, so accepting costs after the
stage consent never invalidates it. A new generation needs a new acceptance. The accepted limits and the payer are in the
lifecycle view (owner-visible); there is no NFT operator console surface yet.

### NFT recipient resolution (RV-L4-02, round 4)

Wallet rows are no longer unique per (chain, address) for unproven External wallets (audit-1 M2), so a transfer resolves
the recipient only from Platform (custodial) wallets (`INftLifecycleStore.FindCustodialWalletIdsAsync`, indexed) and the
signature-proven External wallet named by `IWalletOwnershipProofStore`. An unproven row neither blocks nor captures a
transfer. Two different owners for one address refuse with `NFT_RECIPIENT_AMBIGUOUS`; no candidate refuses before
admission. Hosts without the proof store accept custodial recipients only.

### Ownership rows are record links (SQL-05, round 4)

`nft_ownership.user_id` / `issuer_user_id` are `record<user>`; `CommitAsync` writes `SurrealLink.ToLink("user", …)`.
A bare id was refused by the schema, so every confirmed lifecycle failed to finalize.

### Bindings across a transfer (audit-2 M7, round 4)

A holon/wallet binding outlives an NFT transfer. It grants nothing, and cannot be edited, unless the binding's target is
owned by the current NFT holder; the target's owner may remove a binding left behind.

### Revocation refusals (round 4)

The lifecycle revoke path refuses before any clawback or admission when the issuer is unrecorded, the asset is not
soulbound, the holder address is unrecorded, or the issuer has no wallet on the chain; a lookup failure propagates
(fails closed). Covered by `Credentials/CredentialRevokeRefusalTests`.
