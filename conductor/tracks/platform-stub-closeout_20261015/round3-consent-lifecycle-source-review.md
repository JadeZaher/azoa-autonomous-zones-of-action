---
type: Review
status: bounded-source-accepted-runtime-unrun
---

# Independent consent lifecycle source review

Lane B separately reviewed the first UserExplicit admission and subsequent revoke/expiry source slices: ConsentLifecycleIdentity, command/receipt/transition models, SurrealConsentLifecycleStore partials and ConsentNotificationPlan. No new bounded source blocker remains in these reviewed slices. Admission joins actual owner credential, active tenant, original keyed command, grant, audit and frozen notification. Exact receipt replay cannot recreate a grant after later revocation. Missing destination is explicit and is not retargeted by a future registration.

The registration absence question was checked against the authoritative management path: SurrealWebhookRegistrationStore.Management locks the same target tenant as consent transition planning, while the transaction rechecks absence or exact registration revision. The legacy generic registration writer must remain outside the final authoritative cutover paths.

Revoke and expiry use the original immutable grant snapshot and exact grant epoch. Revocation does not require the recipient tenant to remain active. Expiry checks database time inside the transaction and cannot record after revocation wins; its event timestamp is the persisted expiry time. Immutable transition facts support replay, and historical already-revoked grants explicitly report missing notification proof rather than inventing it. The shared destination planner freezes exact body, destination and audit as part of the parent transaction.

This is bounded source acceptance only. Participation, bulk/offboarding/sovereignty authority, controllers, legacy writer retirement, common leased consent sender, clients and runtime gates are not accepted by this review. Authored model and real-store fixtures have not run. No claim of full consent migration or production readiness is made.

## Participation and offboarding follow-up

Lane B separately checked the membership epoch-before-enumeration closure and ConsentNotificationPlan.ForTransition reuse. No new concurrency blocker was found in the reviewed offboard transaction: each grant carries its original snapshot CAS, while one captured destination fence supplies all grant-specific events without divergent parameter collisions.

An authority blocker remains in the new direct Participation admission wrapper: an owner's login alone cannot prove membership in a caller-selected tenant/reference. Governance consumes Participation as electorate evidence, so arbitrary self-enrollment would widen voting authority. Root accepted the finding and is adding exact tenant-approved subject/reference proof plus actual provisioning-source authority. Participation activation is not accepted until that correction is independently reread. Existing UserExplicit admission and prior revoke/expiry bounded verdicts are unaffected.

Correction reread: Root added ConsentParticipationApproval and the actual tenant provisioning source path. Direct Participation now requires exact active approved subject/membership evidence. Creating approval requires actual tenant TenantProvision authority and transactionally unchanged child OwnerTenantId, ExternalUserId and authentication watermark. Original command replay still returns the historical grant rather than recreating it. The self-enrollment blocker is resolved by source inspection. This extends bounded source acceptance to the reviewed approval/admission/offboard slice; caller cutover, account-claim integration and runtime execution remain outside this verdict.

## Leased delivery and authenticated caller follow-up

Lane B separately reviewed the consent evidence helper, leased delivery store (Evidence, Unavailable and Management partials), delivery worker, lifecycle service, expiry worker, owner/tenant controllers and registrations. No additional blocking defect was found in this bounded source pass. Immutable transition/body/destination proof is checked before delivery; a lease captures the current secret revision without retargeting the original destination. Management returns metadata, binds the original event epoch and preserves lifetime counts across audited retry cycles. The shared sender is called before persistence completion, so delivery-store exceptions are not reclassified as HTTP failures. Expiry facts are recorded independently of HTTP delivery enablement. Caller commands use the actual authenticated principal and persisted request identity.

The suggested removal of SurrealForge.Client.Idempotency was withdrawn: the installed package XML identifies SurrealTransientConflict in that namespace. No import deletion is required. Legacy provisioning/account-claim mutation retirement, remaining client migration and the integrated runtime sweep remain outside this source verdict. No tests or runtime gates were run.

## Provisioning cutover follow-up: authority gap remains

The mandatory atomic issuer wrapper, actual-principal provisioning witness propagation and removal of ConsentManager mutation methods are coherent by source inspection. Identity creation before grant admission can leave an unprivileged account on authority failure; the manager reports that as failure rather than implied consent success.

A separate caller gap remains: WithImpliedGrantAsync deliberately returns historical identity success after account claim, but TenantCustodialAccountManager.ConvergeAccountAsync then calls BootstrapWalletAsync for that user. BootstrapWalletAsync does not carry the tenant witness or serialize wallet creation with current child ownership/claim. Thus historical receipt replay can cause a fresh wallet mutation after claim, including a claim racing between grant replay and bootstrap. Required correction: preserve historical identity replay, while new wallet mutation joins current actual tenant credential ceiling, wallet-management authority, exact child ownership and current provisioning membership. A pre-read alone cannot close this race. Root was notified; this caller slice is not yet accepted. Account-claim consent revocation integration remains separately in progress.

## Account-claim notification transaction review

Lane B inspected ClaimRevocationsAsync and its composition inside SurrealUserStore.TryUpdateAuthenticationAsync. The original user authentication/ownership CAS, exact unrevoked grant-set count, each original grant version/contents, and grant-specific audit/event/proof writes are one transaction. The user-row lock serializes concurrent new grant admission; count plus each original row CAS rejects a same-count set substitution. Per-tenant parameter scopes keep grouped destination snapshots distinct. No additional blocker was found in this bounded source slice. Claim-token consumption-before-persistence retry loss remains separately tracked by root, and the bootstrap-after-claim caller blocker remains open. No runtime tests were run.
