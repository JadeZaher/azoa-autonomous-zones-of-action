---
type: review
status: accepted-bounded-source-only
---

# Shared signing factory and transaction fence review

Root independently reread SigningAdmissionAuthorityFactory, SigningAuthorityFence,
SigningAdmissionAuthority models, ValidatedTokenTime and factory regression cases.
The earlier API-key scope expansion and operator owner-authority findings are
corrected. Exact stored API scopes/key identity/expiry remain pinned while the
credential ceiling is constrained by authenticated scopes. Full login authority
is distinguished from scoped operator credentials and their credential/session
revisions. Delegated authorization selects exact per-scope UserExplicit grants;
the transaction locks those unchanged grants, current user watermark, tenant,
wallet ownership/address and original node witness rather than substituting a
new grant after revocation. Domain callers must use the fence inside their own
first-custody/first-egress mutation and preserve prepared-wire authority evidence.

The core is accepted for domain integration. Factory DI is registered. This is
not acceptance of the full participant-authorization API/scheduler changes or
all domain integrations, which remain separate reviews. No build or tests ran.
