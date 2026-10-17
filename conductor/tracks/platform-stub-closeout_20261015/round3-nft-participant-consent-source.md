---
type: implementation-review
status: pending-review
---

# NFT participant consent implementation

The lifecycle retains original actor authority and a separate append-only history
of participant stage consent. Each entry binds the actual captured credential,
wallet, selected grants, lifecycle, stage, generation, complete transaction intent
and physical protocol. Original actor and participant fences run in the same
versioned acceptance, preparation and first-send transaction. Observation remains
available after revocation.

The public accept request now carries a stable request ID and the exact reviewed
lifecycle version, state, stage and generation. The SDK requires that body; the UI
saves it before sending and retries the same body after a lost response. An old
request can resume its original stage and generation, including a crash after
acceptance but before custody. It cannot authorize a later stage. A changed body
or participant cannot reuse the request ID.

The store rejects rewritten history, duplicate stage/generation entries, reuse of
request IDs, append without the authority fence, and append from Prepared or
Submitted. Replacement consent is eligible only after the previous generation's
recorded authoritative no-effect result. Separate replacement cost authorization
remains unfinished and is not implied by participant consent.

Authored evidence: NftStageConsentTests covers missing/wrong credentials, delayed
stage/version/generation, retained actor identity, lost-response replay, changed
wallet/protocol and history rewriting. SurrealNftLifecycleStoreTests.Participants
covers issuer/participant cutoffs, changed wallet, first-egress refusal after
revocation and observation without replacing consent. SDK and browser fixtures
cover exact request bodies and persistence before HTTP. Existing workflow and
credential acceptance fixtures were migrated to explicit requests.

All runtime gates remain held for the final integrated sweep. Schema emission,
independent source approval, remaining fixture API migration, replacement cost
flow and broader end-to-end verification remain required.
