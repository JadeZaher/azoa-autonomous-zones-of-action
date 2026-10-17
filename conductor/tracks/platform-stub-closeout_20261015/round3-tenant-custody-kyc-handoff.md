---
type: implementation-handoff
---

# Tenant custody KYC admission

SOURCE_VERDICT: ACCEPTED by Root after correction reread. No build, tests, schema generation or runtime gates executed.

The current custody read/bootstrap packet was independently source-accepted by Root. This follow-on closes actual KYC effects: dedicated manager/store entrypoints carry the current tenant credential, child ownership/authentication cutoff, exact original provisioning grant and open enrollment epoch into attempt creation/retirement/document transactions. Hosted first-send authorization joins the same fence and the existing attempt/provider policy fence. The internal witness is never serialized into provider request DTOs.

Inspection found prepared hosted recovery could itself set creation_started_at. That bypass is removed: provider-attempt claims require already-authorized first egress both before and inside their transaction. A prepared request or unsent journal is insufficient to send. Exact already-authorized recovery remains available, and callback accounting does not require renewed consent.

Changed source: IKycManager, new IKycCustodialAdmissionStore and ICustodialHostedSessionStore, KycManager, TenantCustodialAccountManager, HostedSessionRequest/flow, SurrealKycStore plus Custodial partial, SurrealHostedSessionStore, SurrealHostedProviderAttemptStore and the current access fence predicate precomputation. Ordinary create/document statements in the changed admission transaction now use scoped typed writers.

Authored evidence: real-Surreal custody fixture covers current-authority positive create/attach/single first-send, account claim/offboard/grant revocation after capture denying documents/retirement/first-send/new attempt with no partial writes. Existing hosted attempt fixture now proves prepared-only recovery cannot authorize first-send and retains actual authorized concurrent lease/replay assertions. Tenant manager mocks now exercise the dedicated witnessed call contract. Tests remain unrun for the final integrated sweep.

Current direct-user KYC paths stay distinct from tenant custody. The manual provider's pre-write methods perform local validation/session description only; hosted adapters reject direct document-based session creation. Inert prepared/claim rows after refused authorization use existing audited reconciliation; no automatic authority reconstruction is introduced.

Review corrections: unwitnessed tenant Begin/Submit overloads were retired after source-call inventory. Attach-once now uses scoped typed document read/update/creates and explicit atomic assertions; provider-attempt owner locking also uses scoped typed UPDATE ONLY. Runtime remains unrun.
