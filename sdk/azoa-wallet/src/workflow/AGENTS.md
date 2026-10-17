# Workflow admission and recovery

Persist the start idempotency key, economic consent, and preview published-version
hash before sending a start request. Reuse the entire intent after response loss;
creating another key admits another run. Admission returns a durable run identity,
not evidence that its effects completed. Read its status and node outcomes.

Child credentials apply to start, status, advance, and signal. Template
instantiation is an authoring operation under the current principal; persist the
resulting action identity before using it for economic workflow admission.

The fluent template start currently requires explicit instantiate, persist the
action identity, preview, and start calls. Re-instantiating on retry changes the
action-scoped run identity and can duplicate effects. Durable idempotent template
authoring is required work in the platform closeout track's Round 3, not a claim
that the convenience path is complete. Economic consent must always refer to
the concrete action's published version.

### NFT draft parity

Grant mint builders require metadataUri and preserve explicit platform fee and protocol cost authorizations exactly. Creation accepts no imported tokenId. These fields describe consent; they do not authorize a different workflow execution key or replace the server admission fence. Workflow-specific cost disclosure and acceptance must bind the frozen execution lifecycle before preparation.
