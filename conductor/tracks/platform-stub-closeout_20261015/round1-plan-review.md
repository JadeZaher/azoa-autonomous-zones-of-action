---
type: review-evidence
---

# Round 1 adversarial plan review

Reviewed conductor/tracks/platform-stub-closeout_20261015/round1-plan.md after completing the separate adversarial assessment. Read-only implementation review; no tests or code changes in this review pass.

Verdict: direction is sound, but resolve the following acceptance and sequencing gaps before dispatch.

1. **P0 credential guard must be an explicit immediate prerequisite.** The plan says keep the action gate, but the ordinary CredentialController revoke route remains a separate reachable signing surface. First isolate every platform-clawback caller from unproven ownership/issuer rows, including direct controller, action handler, SDK and internal manager. Gate removal requires independently reviewed confirmed issuance provenance. This mitigation can run first without postponing the real implementation.

2. **Define chain asset identity and issuance evidence precisely.** Require chain + network + contract/mint address + token/asset ID + confirmed transaction identity, with unique current ownership and immutable issuer authority. Source acceptance should explicitly reject caller-supplied existing tokens, self-minted registrations and duplicate fabricated rows as revocation authority. Confirm the transaction's actual creator/signer, recipient, quantity and soulbound/clawback parameters; a transaction hash supplied by the request is not proof. Proof must survive restart and operation-row/persistence failure.

3. **Recovery absence is not a live external-request cancellation.** An operator's current search result cannot prove a paused create will never arrive later. Lane A should test operator absence release while creator is suspended before send, during HTTP, after vendor acceptance, and before local attachment. Require a durable local creation ownership/cancellation fence and an aged/quarantined human attestation appropriate to unresolved vendor status. Keep automatic retry excluded while external outcome is ambiguous. Distinguish fresh in-flight CLAIMED from NeedsOperator; current polling should not instantly label every ordinary begin as operator work.

4. **Bank restitution requires an authoritative aggregate cap.** Explicitly test partial Connect reversal, duplicate/reordered payouts/reversals, a payout that becomes paid after an earlier failure observation, and prior unit restitution. Cumulative returned units must never exceed proven returned funding net of previously restored units. Atomic reservation must bind original connected account, environment, funding transfer, payout attempt and unit quantity. The plan's 'platform funds returned' sentence alone does not establish the exactly-once restitution ledger.

5. **Stripe external lifecycle is conditional evidence, not an excuse for a production fake.** Before planned development transactions, establish actual development account permissions, available balance and authorized customer/payment test fixture. If external setup blocks the lifecycle, retain fully implemented code/contract fixtures and record the precise setup blocker; don't silently report fixture tests as provider acceptance. Continue no live-key financial actions.

6. **NFT access must use the authenticated subject at every entry point.** The plan covers ownership/permission checks but should explicitly require no unrelated composite disclosure, no borrowing another user's NFT ID for access checks, no permission grant from a present-but-denied dictionary value, and no stale UserId after transfer. Define whether NFTs convey application access and exactly which confirmed chain/current-owner fact authorizes it before implementing multiple inconsistent policies.

7. **Schema and verification sequencing must leave room for failures.** User policy delays execution until the end, but the final sweep can discover real compile/runtime defects (the current changes already exposed immutable-builder and null-Set hazards in source review). The fourth-round plan must reserve a complete remediation batch and a final evidence-producing rerun when failures require it; 'one sweep' must never mean leaving known failures. Schema generation should precede the final runtime sweep so tests run against the actual generated model. Separate runtime-independent authoring completion from verified goal completion.

8. **No silent loss of wallet-auth and remaining surfaces.** Later-round inventory is acceptable if explicitly assigned round entry criteria and closure evidence. Add the wallet-auth canonical address/nonce/key-uniqueness contract to that backlog now; it is a confirmed implementation gap rather than an item that a later keyword scan may omit. The full-goal scan also needs source-level reachability checks, because the NFT semantic stub did not announce itself with NotImplementedException.

Recommended dispatch after corrections: NFT owner first closes all unproven signing entry points, then implements real lifecycle; identity owner resolves recovery/lineage with independent safety review; Stripe owner implements structured failure/refund/restitution under durable aggregate caps. Central wiring/schema owner and independent final reviewer remain necessary. No author should approve their own transaction invariants.
