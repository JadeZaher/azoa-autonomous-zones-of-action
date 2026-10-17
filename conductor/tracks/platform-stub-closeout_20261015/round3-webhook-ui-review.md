---
type: review
status: corrected-source-accepted-runtime-pending
---

# Tenant webhook editor independent source review

Lane A reviewed the tenant registration component and its response-loss,
secret-storage and unchanged-version fixtures. No runtime checks were executed.

Required correction: a command rejected by the server while the registration
version remains unchanged permanently locks the editor. An HTTPS URL can pass
browser validation but fail the SSRF guard. The command is already persisted,
fields become disabled, retry resends it, and reconciliation refuses to clear it
until a version advances. Provide an authoritative exact-command abandonment or
rejected disposition that serializes against a delayed original mutation, then
allow a replacement command. A transient error or mutable DNS rejection alone
must not authorize local discard. Cover rejected destination followed by safe
replacement and delayed original request in the regression fixture.

When adopting a newer version during reconciliation, clear any memory-only
secret from an earlier result. This matters if local storage removal failed after
success and another session rotated before reconciliation.

The inspected exact-body persistence before send, no send on storage-write
failure, unchanged response-loss retry, secret exclusion from local storage and
account-key remount are otherwise coherent. Operator UI was not part of this
slice and must share the same definitive recovery behavior.

## Withdrawal correction reread

Root added an authenticated exact-command withdrawal operation using the same
unique actor/tenant/request audit identity as mutation. A winning withdrawal
commits a permanent Withdrawn disposition; a delayed mutation cannot commit its
registration change because its receipt create conflicts in the same transaction.
If mutation won first, withdrawal returns AlreadyApplied metadata without secret.
Command-hash mismatch is refused. Current credential/scope and operator recency
remain fenced for a new withdrawal; exact disposition replay does not create an
effect. Both serial orders and the concurrent race have real-store fixtures.

The editor retains the exact saved command after failed withdrawal and unlocks
only after authoritative withdrawal/application disposition and successful reload.
The new fixture covers lost withdrawal response and unchanged retry. Reconciliation
also clears stale in-memory secret. These changes resolve the reported source
findings; this bounded source pass is accepted with runtime verification pending.

## Operator adapter and BFF reread

Reviewed the shared editor's operator transport adapter, tenant dialog mount,
exact registration/withdraw route allowlist, body sanitizer, and new operator
editor/BFF fixtures. Target changes remount the editor and clear in-memory secret;
saved commands have a separate target-scoped operator storage key. The adapter
uses the cookie-backed same-origin operator proxy rather than tenant credentials.
The BFF forwards only the closed command fields, rejects unsafe integer versions,
preserves the original destination string and normalized reason semantics, and
retains the existing same-origin mutation check and no-store response boundary.
No additional concrete blocker was found in this bounded source pass. Fixtures
remain unexecuted; production operator policy enforcement is the server boundary.

## Delivery clients and retry recovery

Reviewed the delivery SDK DTOs and methods, delivery panel and its account/target
mounts, and operator delivery BFF allowlist, paging and retry sanitizer. Exact
commands are saved before sending; a lost response retains the request. Archival
requires an authenticated direct event read with a higher version and does not
claim delivery cancellation. The initial list could overwrite a newer refresh;
the author corrected this with one monotonic read generation shared by all list
requests and a deferred-response regression. The corrected slice is accepted by
the independent lane-A reviewer as source only. SDK replay, UI recovery and BFF
fixtures remain unexecuted until the final integrated sweep.
