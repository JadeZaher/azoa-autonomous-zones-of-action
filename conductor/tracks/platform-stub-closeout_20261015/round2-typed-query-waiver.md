---
type: ImplementationNote
title: Temporary typed correlated tenant-principal query waiver
expires: 2026-11-15
status: Open bounded library gap
---

The existing `SurrealUserStore.ListTenantPrincipalsPageAsync` query retains its single raw SELECT until SurrealForge supports the correlated/subselect membership shape in its typed API. SurrealForge.Client 1.2.0 `SurrealQuery<T>.Where` / `ExpressionTranslator` cannot express `user.id INSIDE (SELECT VALUE user_id FROM api_key WHERE ... scopes CONTAINS ...)` with server-side case-folded username/record-ID search. The source query keeps its active/nonreserved user predicate, live/nonrevoked tenant-provision API-key predicate, search and bounded 1–101 output page. Replacing it with an unbounded application-side key/user join would change performance and weaken the single-query authority snapshot.

This waiver is limited to that pre-existing query; no new CRUD uses it. Owner: platform datastore maintainer. Before 2026-11-15, add a typed correlated membership/search surface upstream or redesign this bounded authority listing and remove the raw call. This is a library-expression waiver, not a stub-completion claim or external delivery deferral. All other touched user CRUD and wallet nonce/claim-token access now use typed keys/writers/CAS, and unexpected store failures retain the centralized exception boundary.
