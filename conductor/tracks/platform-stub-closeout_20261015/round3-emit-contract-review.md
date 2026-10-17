---
type: Review
status: Accepted contract after corrections; runtime unrun
---

# Independent Emit contract review

Reviewer: lane B. Source-only review; no tests or builds run.

The durable-acceptance promise, atomic outbox/receipt boundary, immutable destination, leased at-least-once delivery, encrypted registration secret and shared consent regression scope are appropriate. Existing `ActionWebhookEmitter` swallows enqueue failure and generates new IDs, while `ActionWebhookDeliveryWorker` resolves mutable registrations and has no delivery claim; the amendment addresses those concrete defects.

## Required clarifications

1. **Absent receipt recovery must make progress without blind dispatch.** The proposed resolver only loads a saved receipt. A crash after action admission but before Emit acceptance therefore has no receipt to load. Specify that recovery can retry the same deterministic atomic acceptance under current execution/tenant authority; the receipt uniqueness constraint resolves overlap with a suspended original dispatcher. Deterministic invalid frozen input needs a durable failed receipt under the same command fence. Transient registration/authority unavailability remains discoverable and retryable; it must not manufacture accepted delivery.
2. **Separate destination revision from secret revision.** The contract pins endpoint identity/version but permits current-secret signing. Define distinct immutable destination identity and monotonically versioned registration/secret revisions. Secret-only rotation must not make every queued event fail its destination comparison. URL change, deletion/recreation and disable/re-enable must have explicit behavior: no old event may be silently routed to a new destination, even when the tenant unique key is reused. Retry must validate the exact original destination identity. Include secret revision/key identifier on delivery metadata so receivers can distinguish rotations without changing the stable body.
3. **Define the disable/rotation cut point.** Checking registration then posting is not atomic with management changes. Claim/delivery authorization must lock and capture the current active destination and secret revision together with the event attempt, and registration updates must participate in the same version protocol. An already authorized/in-flight attempt may complete after disable or rotation; expose this limitation explicitly instead of promising recall. No subsequent attempt may use the retired authorization. Include disable-vs-claim and rotation-vs-retry fixtures.
4. **Transport and secret contracts need explicit safe bounds.** The existing SSRF guard permits URL userinfo and IPv6 multicast/reserved ranges because its IPv6 fallback accepts all remaining addresses; reusing it unchanged is not sufficient for the amendment's public-address guarantee. Require validated credential-free destination URLs, no ambient proxy/default credentials/cookies, normal TLS host validation, bounded request body and response headers, and response-header-only completion so an unlimited 2xx body cannot hold the worker. The encryption seam must bind ciphertext to tenant/registration/purpose (authenticated associated data or equivalent authenticated envelope), preventing ciphertext swaps between registrations. Consent delivery must consume that same binding.

## Acceptance additions

- Action admitted then process dies before acceptance: exact recovery accepts once, with concurrent original/recovery contention covered by a real-store fixture.
- Secret-only rotation preserves queued event delivery to the same destination; destination replacement/recreation cannot take over old events.
- Authorized in-flight attempt versus later disable has the documented outcome; stale holder cannot persist completion or authorize another attempt.
- Reject URL credentials, multicast/reserved/private targets and proxy bypass; no response-body allocation is required for a 2xx acknowledgment.
- Swapped encrypted secrets across tenants/registrations fail authentication; one-time secret disclosure is absent from logs, lists, retry audits and normal registration reads.

After these contract clarifications, the proposed implementation may proceed to independent source review. This is not runtime approval.

## Corrected contract reread

Root's binding-corrections section explicitly resolves all four requests: deterministic
atomic absent-receipt recovery/rejection, separate destination/secret revisions,
serialized attempt authorization with disclosed in-flight limits, and bounded transport
plus identity-authenticated encryption. CONTRACT_VERDICT: ACCEPTED for implementation.
The required concurrency/transport fixtures remain unrun and source review follows authoring.
