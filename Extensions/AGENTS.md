---
type: doc
scope: Extensions
---

# Extensions — dependency registration boundaries

## §surreal-authentication-scope

`AddSurrealForge` owns the application-side transport workaround for
SurrealForge.Client 0.2.0. When the selected connection section explicitly sets
`AuthenticationScope=Database`, its named HTTP client sends
`Surreal-Auth-NS` and `Surreal-Auth-DB` from that section's namespace and
database. Unknown scope values fail during registration. An omitted scope adds
neither header so root-based Development and legacy test configuration retain
their package-native behavior.

Remove this workaround only after the consumed package exposes and proves the
same explicit database-authentication contract.
