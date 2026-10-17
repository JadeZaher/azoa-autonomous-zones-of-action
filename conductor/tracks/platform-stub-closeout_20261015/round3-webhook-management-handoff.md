---
type: implementation
status: awaiting-independent-source-review
---

# Atomic webhook registration management

New command/receipt DTOs, WebhookRegistrationAudit POCO and partial store management
method commit current credential authority, active tenant lock, exact registration
CAS and unique audit receipt together. Tenant direct credentials require tenant:
provision; child contexts are refused. Reserved node operator requires its current
scoped credential/bootstrap revisions and five-minute auth recency at mutation.
Create/update/disable/rotation use separate destination and secret revisions.

Request ID is actor/tenant scoped and full canonical command hash must replay.
Only a winning original commit returns newly generated secret; replay returns the
original metadata receipt with NewSecret=null, and a lost secret response needs an
explicit new rotation. Transient retries reuse the same ciphertext and mutation.
No audit stores the secret or ciphertext. Current tenant row must remain active.

Added realDB cases concurrent exact replay has one secret and one receipt, request
conflict, cross-tenant denial and revoked actor rollback without registration/audit.
All unrun. Public controller/service, operator discovery, SDK/UI and delivery
attempt leases remain required; no completed webhook-management claim yet.

## API and SDK integration

WebhookRegistrationManagement captures the actual authenticated credential and
selects tenantProvision or operator consent. Tenant routes have no caller-supplied
tenant ID; child contexts are refused. Operator routes explicitly target a tenant
and require recent operator policy plus the stricter five-minute service/store
check. GET maps an explicit metadata DTO with no ciphertext/plaintext fields.
Both controllers set Cache-Control: no-store, including mutation responses.
SDK methods cover tenant/operator GET/PUT and preserve exact command retries.
Authored service cases cover metadata nondisclosure, arbitrary operator-target
refusal from ordinary login, and actual scoped child refusal. SDK cases cover
unchanged retry after response loss and operator path escaping. No execution.
UI and full auth/HTTP fixture coverage remain required.

## Legacy and disable review corrections

Explicit ReplaceLegacy plus LegacyFingerprint binds the exact GET snapshot. The
fingerprint is purpose-qualified HMAC using the platform encryption facility. A
legacy mutation checks stored fields inside the authority/receipt transaction,
generates fresh secret material, sets valid revisions and unsets the plaintext
column. Replay returns original metadata without another rotation/disclosure.
An unchanged denied URL may be disabled; enabling or replacing URL still requires
validation. RealDB cases cover fingerprint refusal/replacement, plaintext removal,
exact replay and blocked endpoint disable/re-enable refusal. All unrun. SDK types
carry the explicit legacy consent fields.
