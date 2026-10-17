---
type: implementation-evidence
---

# Round 1 Lane A — authored, awaiting independent approval and final sweep

Independent review of the prior KYC/KYB batch: `.omc/research/round1-kyc-kyb-static-review.md`.

## Invariants changed

- Domain KYC attempts persist before vendor creation. Attempt id scopes hosted id
  and admission key; retry reuses it, renewal allocates a new attempt. Retirement
  and redirect attachment use conditional writes, preserving concurrent outcomes.
- Before provider send, creator-token ownership and current unexpired attempt are
  fenced atomically with tenant selection/provider trust revisions and authority
  guard writes. Node configuration remains process-owned resolved configuration.
- Ambiguous sent requests quarantine without releasing their admission. Only
  provably unsent requests release. Same-attempt retry reclaims the audited row,
  rotates creator token, and increments the version; old creators remain fenced.
- Parent and child aliases reserve deterministic keys in the session attachment
  transaction. Alias conflicts roll back attachment. Operator recovery accepts
  recognized pending results and canonical ids; arbitrary aliases are refused.
- Legacy callbacks before domain attachment remain retryable. Both screening and
  ongoing monitoring must carry the approved attempt's verified-subject lineage.
  Missing-lineage real approvals project expired and can reverify through begin.
- Immutable AML mutation assignments and flat erasure transactions now retain their
  actual field updates; multi-statement result indices include BEGIN and LET slots.
- KYC create tenant/provider/attempt authority guards use distinct parameter names;
  SurrealForge Combine merges bindings and never renames them.

## Authored regression coverage (not executed)

- `HostedSessionReconciliationTests`: sent quarantine + late original response;
  unsent release fences suspended creator; legacy unknown send stays reserved.
- `HostedSessionRecoveryContractTests`: pending KYB existence without approval;
  malformed result refusal; distinct parent/child query bindings.
- `KycHostedSessionOutcomeHandlerTests`: callback-before-submission retry followed
  by approval; monitor lineage fixtures updated.
- `KycManagerHostedProviderGateTests`: retry reuses attempt; rejected, expired and
  completed missing-lineage approvals renew with different durable correlations.
- `SurrealHostedSessionRecoveryTests`: real generated-schema persistence for alias
  collision rollback and alias lookup, quarantine/late attach, release/reclaim token
  fencing, expired/changed/disabled authority send refusal, AML monitor follow-up,
  poll failure/success state and bounded two-table erasure.

No build, test, lint or schema-generation execution occurred in this lane. Root
owns independent review and integrated validation. New alias POCO and hosted fields
require normal generated-schema refresh in that sweep.

## External-only condition

Identomat's documented API does not provide authoritative lookup by AZOA attempt
id or a barrier proving an earlier send cannot later arrive. Automatic release of
an uncertain sent request is therefore unavailable. The implemented operational
path preserves quarantine and permits original-response or verified operator
attachment; absence alone never makes sent money/identity side effects disappear.

## Reclaimed creation age

CreationClaimedAt records each fresh generation independently of immutable CreatedDate.
Flow, KYC/KYB status and aged-admission queues use this timestamp with a legacy fallback.
The real-persistence reclaim regression asserts the newly reclaimed request is not aged.
Schema regeneration and execution remain part of the integrated final sweep.
