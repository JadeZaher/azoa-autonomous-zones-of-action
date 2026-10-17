# Identomat KYB adapter

See `Services/Kyb/AGENTS.md` for subject ownership, trust policy, callback routing and vendor
configuration. This provider reuses `IIdentomatHttpTransport` so KYC, KYB and AML share the
vendor's request budget. It never accepts caller-supplied API origins, workflow IDs or credentials.
Only authoritative result steps can produce a terminal approval; callback contents are discarded.
No personal beneficiary fields or bearer session URLs are copied into business evidence.
