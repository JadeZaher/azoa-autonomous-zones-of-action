# KYC provider boundary

The provider advertises availability and flow shape before Azoa writes an active
submission. A configured-but-unavailable adapter fails closed; it never leaves a
pending row that no provider can process.

The manual provider accepts document references only. References must be
absolute HTTPS URLs without credentials, query bearer tokens, fragments, or
loopback hosts. Raw bytes and arbitrary metadata are not accepted through the
tenant contract. Manual approval/rejection remains on the JWT-only `Operator`
surface; a tenant API key can begin/submit but can never self-approve.
It is registered only in the Development host environment. Base/production
configuration selects an unavailable provider, and an explicit production
`Kyc:Provider=manual` still resolves to unavailable until Azoa owns a private
upload lifecycle, malware scanning, retention/deletion, and reviewer operations.

Hosted adapters return non-sensitive session/redirect metadata from `Begin`; no
provider credentials belong in that response, and the tenant projection omits
the provider session id. The Veriff adapter remains an
explicit unavailable stub until a reviewed integration exists.

Provider selection is strict: `manual`, `veriff`, and `hosted`/
`generic-hosted` are the only registered keys. Any other `Kyc:Provider` selects
`UnavailableKycProviderService`; it never silently inherits manual review.

`GenericHostedKycProviderService` is an operator-configurable, unavailable
scaffold. Even complete configuration does not advertise availability. A real
adapter must first add a durable, idempotent verification-attempt record,
persist the hosted session before returning a redirect, verify signed webhook
headers over the raw body, deduplicate provider event ids, map events to the
attempt/avatar, and CAS terminal status. Until that lifecycle exists the stub
prevents the current begin/submit shapes from creating two provider sessions.

Only `MANUAL` submissions may be decided by Azoa operator approve/reject.
External providers must decide through their future verified-event path. An
expired approval is not verified and expired active submissions can be replaced.
Submission plus document rows are created in one Surreal transaction.

Manual review uses `IKycStore.TryReviewAsync`, a conditional update whose WHERE
clause requires provider `MANUAL`, active status, and unexpired timestamp. Two
reviewers may read the same pending row, but only one transition returns a row;
the loser receives a conflict. No `Avatar.IsVerified` projection is updated—the
submission ledger is the sole authority, so a partial second write cannot split
identity state from the KYC gate.

Begin is an ensure-active operation backed by the same KYC ledger. It persists a
pending attempt with secret-bearing provider correlation kept server-side and
returns only safe flow metadata. An unexpired attempt is resumed; rejection or
expiry allows the node to create the next attempt while the tenant keeps one
stable idempotency key forever. Manual approval is refused until documents have
been atomically attached. Hosted providers remain disabled because their future
adapter must claim durable admission before making an external create call.
