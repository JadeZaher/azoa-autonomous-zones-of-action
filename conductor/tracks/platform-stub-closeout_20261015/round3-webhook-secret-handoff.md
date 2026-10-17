---
type: implementation
status: awaiting-independent-source-review
---

# Protected webhook registration storage

WebhookSecretProtector uses the platform AES-GCM facility with an authenticated
purpose/tenant/registration/secret-revision JSON envelope. No plaintext fallback.
WalletKeyService rejects missing/blank keys and exposes explicit envelope helpers.

The public registration POCO now stores encrypted_secret, version and independent
destination/secret revisions. The store uses typed reads/create/conditional update,
removes its duplicate private POCO, raw CRUD and broad exception swallowing, and
returns metadata with the transient Secret input empty. Create/update conflicts
are explicit. Both workers decrypt only at HMAC use. DI is wired. Existing legacy
rows are refused until explicit replacement; schema generation remains unrun.

WebhookSecretProtectorTests author random ciphertext, exact identity/revision,
wrong key/purpose, tamper/plaintext refusal and missing/blank key cases. No tests
executed. Real-store concurrency/secret-nondisclosure fixtures remain required.

This is storage integration, not completed registration management or delivery.
Audited management commands, tenant authorization, secret one-time responses,
exact idempotent retry, attempt leases and retained revision snapshots remain
required. The old internal RotateSecretAsync cannot be exposed as a public route
because it lacks the management expected-version/audit envelope. No public route
has been added. Worker broad exception boundaries are replaced in the lease wave.

## Persistence fixtures and review corrections

SurrealWebhookRegistrationTests now covers actual stored ciphertext, ordinary JSON
nondisclosure, tenant isolation/cross-envelope refusal, competing tenant creates,
secret-only destination stability, stale update refusal and concurrent same-version
secret/destination changes with one winner. These use generated schema and remain
unrun until the integrated sweep. Identifier mapping now uses SurrealRecordGuid.
Independent source review identified a missing Idempotency namespace and explicit
Build() needed for the typed update builder; both are corrected.
