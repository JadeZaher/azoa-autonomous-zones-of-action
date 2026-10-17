---
type: Review
status: Corrected bounded source accepted; runtime pending
---

# Protected registration source review

A independently inspected WebhookSecretProtector, WalletKeyService's envelope
helpers, registration POCO/domain mapping/store, worker HMAC use and protector
fixtures. No code was executed.

Required source corrections:

- The registration store needs the SurrealForge.Client.Idempotency namespace
  for SurrealIdempotencyLedger. Core.Surreal does not define that class.
- ExecuteAsync must receive update.Build(), not the TypedUpdateOnlyBuilder.
  The pinned API accepts SurrealQuery; the builder exposes Build.
- UpsertAsync must return an ordinary validation failure for a supplied secret
  outside its documented bounds. It currently calls Protect, whose expected
  ArgumentException escapes this store contract for invalid user input.

Root corrected all three findings and added the real-store concurrency and
nondisclosure fixture. A separately reread those changes. A further concurrency
finding was corrected by retrying only recognized transient serialization
conflicts around the same prebuilt create/update query and ciphertext, with
EnsureAllOk inside the retry. Expected version and revisions never regenerate
during retry; unique create and empty update results remain typed conflicts.
The bounded crypto/storage source slice is accepted after these corrections.

The bounded crypto design otherwise preserves authenticated tenant,
registration, revision and purpose binding, randomized GCM nonces and wrong-key
or tamper refusal. Domain JSON ignores both transient plaintext and ciphertext.
Creation and update use typed writes, unique conflict translation and expected
version predicates; destination and secret revisions advance separately.

This review does not approve management authorization or delivery durability.
Explicit legacy replacement, versioned retained secret snapshots, audited
idempotent management, real-store concurrency/nondisclosure tests and leased
delivery remain required implementation packages. No completed registration
management or Emit capability is claimed.
