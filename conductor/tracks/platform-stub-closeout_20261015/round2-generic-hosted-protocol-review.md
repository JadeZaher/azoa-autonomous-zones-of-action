---
type: Contract Review
title: Independent generic-hosted v1 contract review
status: Corrections required before trust implementation
---

The root reviewer read the proposed protocol and existing shared outcome/person-binding code independently. Operator-fixed trust, exact subject/tenant/attempt/provider correlation, authenticated callback-as-trigger, authoritative result fetch, independent AML and no human-absence release are sound boundaries. This defines an explicit interoperability protocol; it does not pretend existing vendors implement it.

Required corrections:

1. Permanent idempotency and linearizable attempt lookup remove the Identomat-specific automatic-recovery obstacle. Implement automatic same-key authoritative reconciliation/replay using the original persisted request, rather than retaining manual-only attachment as the initial generic implementation. Never allocate a new key to clear ambiguous egress. Contract fixtures must prove lost-response and concurrent recovery create one external session.
2. Define cross-language canonical request equality and freeze the normalized request/key before egress. Use a documented canonical format or explicit field equality; configuration changes cannot reconstruct a different request under an admitted key. Reject unsafe session identifier/path forms or percent-encode the whole path segment without changing authority/origin.
3. Specify webhook key rotation with an explicit key identifier, current configured key and bounded grace for retired keys. Unknown/expired identifiers fail authentication; the five-minute signed timestamp window still applies. Credentials rotate without silently becoming a different provider identity or weakening persisted policy correlation.
4. Clarify pending/review results versus terminal evidence: undecided sessions cannot require decision timestamps. Terminal results need stable decision identity, valid decision-time bounds and an immutable evidence digest/content rule. Contradictory terminal evidence must not replace prior accepted evidence or become a new approval. Cover each terminal state and missing/unknown fields in protocol fixtures.
5. Preserve provider-neutral person uniqueness. Existing `KycIdentityBindingService` derives scoped/global person keys from document number and issuing state, independent of vendor. Replacing hard-coded Identomat attribution must use the validated provider discriminator for provenance while retaining this identity recipe. A new provider cannot create a separate person namespace for the same evidence.

The reviewer requests a bounded amendment reread after these corrections. Wallet/config work may continue independently; the new generic provider trust implementation awaits acceptance. No tests or runtime acceptance occurred.
