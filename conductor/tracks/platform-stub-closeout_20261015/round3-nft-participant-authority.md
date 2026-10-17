---
type: design
---

# Original NFT participant authority through continuation

The existing direct NFT routes check current HTTP scopes and pass actor/tenant IDs, but the immutable lifecycle command does not retain the credential and exact selected grants. Preparation and first-send currently fence workflow execution, while direct operations reconstruct a SigningContext from identities. A later credential must not silently replace the original authority for an already admitted effect.

Thread the actual captured SigningCredentialWitness through direct mint/move/revoke/refund callers, using the existing server-only AZOARequest.AuthenticationWitness for manager adapters. Bind the original actor's wallet and exact operation scope with SigningAdmissionAuthorityFactory before fresh admission; persist that SigningAdmissionAuthority in the immutable command. Workflow calls retain their original WorkflowSigningWitness and bind the same wallet under it, without substituting an HTTP credential. Public request models never supply these witnesses. Receipt replay remains before mutable authority checks and never recreates admission.

At initial admission and every new preparation/first-egress transition, atomically check the original actor authority, exact wallet and selected grants. Original workflow checks retain exact execution identity. Observation/final accounting remain possible after revocation and do not obtain signatures. Missing historical witnesses cannot authorize a new signature; those rows need explicit participant recovery rather than fabricated owner credentials.

Stages requiring another participant record a separate immutable consent bound to lifecycle, stage, generation, exact intent hash and wallet. Its current authority is checked at stage admission, preparation and first egress alongside the unchanged original actor authority. Recipient acceptance never rewrites the original command or expands platform fee/cost ceilings. Platform-signed credential stages still require the real issuer's original authority and actor-paid fee authority; a platform key is execution machinery, not user consent.

Implement stage replacement with an explicit fresh quote/acceptance for the exact next generation after authoritative no-effect. Preserve original receipt, preparation history and participant records. A changed platform authority, wallet, recipient or cost policy requires new explicit review and must not reuse a quote for a different full intent. An expired offer may be retired under the same atomic absence/version rule as workflow cost acceptance.

Required evidence: direct API key/JWT/tenant grant revocation before preparation and first send; replay after revocation without signing; changed actor wallet; recipient credential cannot replace original actor authority; recipient acceptance racing revocation; observation after revocation; exact workflow witness retained; platform authority rotation/replacement generation requires fresh consent; legacy unwitnessed recovery cannot sign. Implement and review before integrated gates.
