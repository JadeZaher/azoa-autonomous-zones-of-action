---
type: directory-doc
---

# Node-admin integration flows

Use the production auth pipeline and generated schema in a unique SurrealDB namespace. Helpers sign the server's
exact returned strings with real Ed25519 keys and compute real TOTP codes. Settings are supplied before host startup.

`NodeAdminAuditSnapshotTests` wraps the executor only to insert a real committed audit append immediately after
the verifier reads its head. Every read and append still executes against SurrealDB. This deterministic interleaving
proves the captured sequence bounds the real typed query; timing sleeps and fake query results would miss the race.

The legacy stale-login comparison first proves a backdated token is rejected by
the seeded operator's authentication watermark. It then temporarily moves only
that fixture user's watermark before the login, retaining the stale `auth_time`,
and restores the original watermark in `finally`. This distinguishes invalidated
credentials from the legacy route's recent-login policy without changing either
production security boundary. Audit assertions use the live route template's
case (`POST /api/Search/operator`), including actor, outcome and PII exclusions.
