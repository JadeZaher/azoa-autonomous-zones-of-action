---
type: implementation
---

# NFT public protocol cost offers

Source implemented; independent review requested from the Hold/Identomat lane. No build or test execution in this batch.

- Shared NftLifecycleIntentFactory preserves the preparation intent and is also used for quotation, including credential signer versus payer separation.
- QuoteMintCostsAsync checks the exact draft/key/participants, existing admission replay, value access, governance, platform fee acceptance and wallets. Provider estimates become private immutable five-minute receipts; quotation neither admits execution nor calls custody.
- Existing admitted requests return only their original matching receipt. Changed drafts and cancelled keys refuse. Admission retains its atomic database-time receipt expiry fence.
- UserNFT and Nft mint-cost-quote endpoints share mint scope; Credential issue cost-quote preserves manager role, distinct recipient and normalized soulbound flags. Responses include the exact receipt/hash/category-limit acceptance payload.
- NftMintRequest and its shared projection forward explicit protocol cost acceptance; null serialization stays omitted.
- Authored service fixture checks no admission/signing during quotation, same intent on later preparation, exact receipt propagation and unchanged replay without a new provider quote.

Remaining implementation: SDK/browser consent surfaces, replacement-generation consent, direct NFT original credential fencing review, protocol fixtures and integrated gates. These are implementation tasks, not external deferrals.

## SDK and browser implementation

SDK exposes quoteNftMintCosts, quoteUserNftMintCosts and quoteCredentialIssueCosts with the original request key, receipt types and explicit authorization fields. All category amounts remain decimal strings. Quote methods never call mint. Authored SDK fixtures cover full-width limits and identical retry bodies.

The UserNFT Solana mint form locks and persists the draft/key before quoting, persists the returned offer without accepting it, displays the independent network/rent/levy limits and payer, and requires a subsequent click to accept and mint. Acceptance is saved before HTTP; reloads and retries preserve that exact receipt. Expired unaccepted offers are refreshed for another explicit review. Accepted receipts are never silently replaced. Account changes clear the prior in-memory request, and delayed results must still belong to the same request reference. Authored UI fixtures cover review without mint and accepted-receipt retention after lost response/reload.

Source review requested independently; all fixtures remain unrun until the integrated sweep.

## Workflow contract correction

The workflow SDK grant builder previously omitted required metadataUri and discarded fee consent. NftMintRequestParams now requires metadataUri, excludes imported tokenId for creation and forwards platform/protocol cost authorization unchanged. Serialization fixtures were authored and the existing grant fixture updated; no gates run. This does not close workflow Solana mint: a parked cost-disclosure/explicit-acceptance path must bind the actual frozen execution lifecycle and original authority. That remains implementable root-owned work, not an external deferral.

## Independent review correction

Reviewer found recipient/default wallet or credential authority changes could strand an already admitted request. New receipts now carry a canonical initial-intent commitment plus recipient wallet identity. Mint recomputes it from current resolved participants and signing authority before Claim; mismatch refuses before admission, permitting same-key re-quotation. Historical null fields remain omitted and original admitted replay remains first. Authored regressions cover changed recipient and changed platform authority without admission or custody. Correction re-review requested; gates unrun.
