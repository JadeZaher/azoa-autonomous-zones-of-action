---
type: feature-spec
status: in_progress
---

# Tenant economic workflow AI designer

Owner requested a parallel session on 2026-10-18, with frequent reviewed integration opportunities into the node-admin release. This worktree starts at `649d501c`; release fixes remain owned by the main session.

An authenticated DApp developer can interview an AI assistant inside the existing Action builder. The intake covers purpose, participants/entity types, countries/states and tax residence, assets/amounts/fees/custody, allocations and tax records, approval/refund/dispute/failure paths. Unknown jurisdictions or economic terms require follow-up rather than invented conclusions. The assistant challenges missing assumptions and highlights legal and tax questions with explicit geographic limits and official references.

The service is stateless and accepts only the current caller's explicitly submitted intake and conversation after `TenantAuthority.CheckAsync` proves an active owner and live provisioning credential. The UI explicitly selects the server-derived owned tenant context; the server compares it with the authenticated owner and repeats authority proof each request. It never reads other tenants, executes tools, saves definitions, publishes, starts runs, or handles signing keys. The server validates proposed node types, configs, graph structure and bindings, and checks resource references against a user-confirmed handle allowlist before offering import. Separate user confirmations of jurisdictions and economic terms are required; conservative obvious-uncertainty checks and model followups do not claim semantic completeness. Import is an explicit human action into the existing editable canvas; normal save, validation and publish controls remain authoritative.

Provider credentials are server-only, configured through ignored local environment injection from the owner's Pi agent JSON. No browser or committed credential. Consent precedes provider egress; no personal identifiers, financial account numbers or keys should be submitted. Requests, transcripts, raw model replies and provider response bodies are not logged by this feature. Per-identity request limits, bounded history, response size and output tokens contain spending; limits are per API instance and do not claim a shared fleet dollar budget.

Acceptance: iterative questions and complete-intake gating; unsupported/invalid model output refuses import; clear disabled/provider-failure states; account changes discard prior interview; browser renders model output as text; legal/tax sources label US-only applicability; server cancellation and model transport failures preserve centralized exception boundaries; tests exercise these controls. Add UX research scenarios in this track, then coordinate integration with the root UX lane.
