---
type: implementation-handoff
status: awaiting-independent-client-review
owner: root
---

# Audited delivery retry and discovery

The backend exposes tenant-bound metadata and explicit audited retries of exact
dead-letter versions. Retry preserves the event body, identity, destination and
lifetime attempt count; a separate bounded cycle restarts. Current authority and
the original active destination are required in the atomic mutation. Independent
backend review is recorded in round3-webhook-delivery-source-review.md.

SDK methods and tenant/operator panels now expose list, detail and retry. The panel
persists the exact request before sending, recovers response loss using that same
request, and archives a stale request only after an authenticated direct read proves
a strictly newer event version. Reconciliation does not cancel delivery. Operator
routes accept only exact GUID paths, bounded paging and the three retry command
fields; authority and destination overrides are discarded.

Authored checks cover concurrent retry receipts, changed destination refusal,
cross-tenant isolation, SDK response loss, client persistence/reconciliation and
operator path/body/query restrictions. All remain unrun pending the final integrated
sweep. Lane A has been asked for an independent client review. Consent delivery
lease parity and retained Emit compensation remain implementation work, not deferrals.
