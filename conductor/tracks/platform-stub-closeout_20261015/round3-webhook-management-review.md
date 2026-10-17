---
type: review
status: corrected-source-accepted-runtime-pending
---

# Managed webhook registration independent source review

Reviewer: lane A; author: root. No builds, tests, schema generation or delivery
execution were performed.

Reviewed the management partial store, command/receipt/status DTOs, audit POCO,
management service, tenant and operator controllers, SDK methods/types and the
new service, SDK response-loss and real SurrealDB management fixtures. The shared
credential factory/fence was inspected at the relevant consent boundary.

The bounded new-registration and versioned-update path has no additional concrete
source blocker: direct own-tenant credentials and the reserved operator are
separated, delegated children are denied, target activity and live credentials
join the exact mutation and unique receipt transaction, generated ciphertext is
stable across conflict retries, and only the original winning commit returns a
secret. Exact replay returns immutable metadata without regenerating or exposing
the secret. Public status explicitly excludes both protected fields; responses
set no-store. SDK retries preserve the original command body.

Required remaining management correction: both MutateAsync and UpsertAsync reject
legacy rows with missing version/revisions/encrypted secret, but no authorized
replacement operation exists. A preserved legacy registration therefore cannot
be rotated, disabled or replaced using this surface. Add explicit audited legacy
replacement bound to an exact original fingerprint/version; remove any historical
plaintext in the same mutation, preserve tenant uniqueness, and test stale repair
and concurrent replacement. This is implementation work, not an external blocker.

Additional usability correction: destination validation currently precedes all
new commands, including disable. An unchanged destination that becomes prohibited
cannot be disabled without choosing another URL. Permit disabling the exact
unchanged destination without making it newly deliverable; keep changed URLs and
activation subject to the full destination policy.

The inspected real-store fixtures cover exact concurrent replay, one disclosed
secret/receipt, conflict and revoked/cross-tenant authority. Final verification
still needs the integrated runtime sweep and operator/API-key cases at the actual
authorization boundary. Delivery leases and UI are outside this bounded review;
this report does not claim completed Emit behavior.

## Correction reread

Root added explicit ReplaceLegacy plus a keyed HMAC snapshot fingerprint in status
and command contracts. The transaction compares the original tenant, URL,
plaintext/ciphertext, revisions, activity, creation and rotation values, then
replaces the secret with a fresh authenticated envelope and unsets the historical
plaintext field. The same receipt protocol prevents redisclosure on replay.
Legacy snapshot fingerprints are not an unkeyed oracle for old secrets. The
new fixture covers wrong fingerprint refusal, plaintext removal and replay.

Exact unchanged destinations can now be disabled even when their current policy
check fails; activation and changed destinations still require the full guard.
The focused fixture distinguishes disable from later re-enable. These corrections
resolve both reported source findings. No additional concrete blocker was found
in this bounded reread; the runtime and broader delivery/UI gates remain pending.
