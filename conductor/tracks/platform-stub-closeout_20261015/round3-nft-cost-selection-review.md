---
type: review
status: source-accepted-bounded
---

# NFT cost selection service review

Reviewer: Lane B, independent of the author. Runtime: NOT RUN.

SOURCE_VERDICT: ACCEPTED for the bounded receipt-selection integration. This is
not provider or public quote-flow completion.

Inspected NftLifecycleService fresh Mint and existing-operation replay, preparation
generation gate and stage intent, NftTransactionIntent, NftLifecycleCommand,
UserNFTMintModel, NftProtocolCostContract, NftProtocolCostAdmissionFence, Program
registration, NftLifecycleHarness and UnknownAcceptedProtocolQuoteCannotAdmitOrPrepareMint.

The existing operation is resolved before loading a current cost receipt. Exact
original request comparison includes the explicit authorization, so another quote
cannot replace the admitted one. The service reads the selected receipt with actual
actor/tenant scope, checks its exact hash and independent limits, then persists it
with the command. The separately reviewed store fence binds draft, lifecycle,
payer wallet, participants, network and database expiry at initial admission.

Only Create receives this quote. Recipient acceptance and subsequent credential
delivery do not inherit the actor's Create quote. The preparing-state generation
check blocks fresh Solana Create without matching accepted generation while leaving
already persisted Prepared/Submitted wire on the original recovery path. Null cost
properties are omitted from request, command and transaction serialization, preserving
historical absence for hashes and replay comparison.

No new blocking source defect was found in this slice. The authored negative
fixture checks no admission, preparation or submission for an unknown receipt.
Final evidence still needs positive selected receipt propagation, expiry/replay,
historical null-serialization byte identity, and later-generation refusal cases.
Provider validation, public quote production, renewed-generation consent and
recipient cost journeys remain explicit unfinished implementation outside this
bounded acceptance; their absence must not be reported as full NFT cost support.

## Public offer and client follow-up review

SOURCE_VERDICT: ACCEPTED for the bounded expanded public offer slice after the
quoted-intent correction below. Runtime: NOT RUN.

Inspected NftLifecycleService.Costs, NftLifecycleIntentFactory, the three public
quote routes and request projections, NftProtocolCostOfferTests, the Solana cost
evidence intent check, SDK quote methods and client.nft-costs tests, and the UserNFT
mint form, shared category disclosure and page fixtures.

The shared intent factory preserves stage signer, issuer, actor fee payer and
participant signing contexts. The quote does not admit or sign. Client acceptance
is a separate action and is persisted before mint submission; response loss keeps
the same request and accepted receipt. Account/request identity checks prevent a
late response from replacing the current request. Category quantities remain
strings throughout the SDK and UI.

The initial review found a recoverability defect: Solana PolicyEvidenceJson binds the full quoted
intent, but fresh lifecycle admission checks only public draft and payer identity.
A changed recipient default wallet or credential platform authority can therefore
produce a newly admitted command whose intent fails the provider's immutable quote
check. Repeated execution cannot repair that admitted command. Bind the canonical
quoted intent digest in the receipt and compare the newly constructed initial intent
before Claim (or provide an equivalent provider validation seam). A changed binding
must refuse before admission so the same unadmitted request can obtain a new quote.
The author added null-omitted QuotedIntentHash, binding the shared initial intent
and recipient wallet identity, and compares it before Claim. Independent reread
confirmed the mismatch refuses before admission; authored recipient-wallet and
platform-authority change fixtures assert no admission/custody and same-key fresh
quotation. The new field is omitted when absent, preserving historical serialization;
old admitted receipt replay still precedes fresh quotation checks.

Rotation after admission but before preparation remains a broader original-signer
recovery concern: preparation currently reads live platform authority. That is not
resolved by the pre-admission check and belongs with pinned-authority and replacement
recovery coverage before full lifecycle completion.

Direct credential witness fencing, frozen workflow cost acceptance, later-generation
consent and final runtime evidence remain explicitly separate required work.
