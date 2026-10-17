---
type: Review
title: Round 2 contract coordinator review
status: Accepted with explicit implementation conditions
---

## Generic hosted identity

Independent reread accepts the revised protocol for implementation. It now requires automatic original-key lookup/replay, exact persisted request bytes and origin binding, bounded callback-key retirement, immutable terminal evidence, and provider-neutral person uniqueness. Expiry may prevent an initial approval; it cannot retrospectively rewrite an already applied terminal outcome. Source and runtime acceptance remain outstanding.

## Action recovery

The complete handler matrix and shared envelope are accepted with these clarifications incorporated before adapters: serialize Version=1; define canonical frozen input and intent bytes; persist them before admission; reject unknown versions/domains and changed actor, tenant or intent. Recovery cannot reconstruct intent from the latest mutable graph. Observation and replay of already authorized wire are distinct from new local mutations, which require current actor/tenant authority without a fabricated principal.

Legacy Running records without receipts/envelopes require evidence-based operator reconciliation, never automatic reset. A workflow promising settlement cannot relabel an unsigned swap build as success. Separate review of local mutation receipts and concrete governance authority/finalization remains required before broad replay or gate removal.

## Deployment

The typed artifact, runtime invocation and native recovery amendment is accepted after two concrete corrections, with source authoring authorized once incorporated. The generated loader must accept deploymentId at invocation rather than inject a deployment ID into bytes hashed before deployment. Composite node identity must include a stable occurrence/index path so repeated source actions and nested occurrences cannot collide. Shared operation references include explicit Version=1.

Source review must inspect account reuse against exact admitted intent, deployed EVM code evidence including immutable/link references, and the real local ELF fixture. This review approves design, not runtime verification; no compiler, test, schema generation or container protocol gate has run for this batch.
