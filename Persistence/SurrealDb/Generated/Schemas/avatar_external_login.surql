-- ============================================================
-- Table: avatar_external_login
-- Aggregate: AvatarExternalLogin (federated (provider, subject) -> avatar binding)
-- Guardrail: G6 SCHEMAFULL
-- Note: Binds a federated identity to an avatar. `subject` is the PROVIDER'S STABLE ID (Google `sub`, GitHub numeric `id`) and NEVER the email: emails are mutable, reassignable, and on some providers unverified, so keying on one lets a reassigned mailbox inherit an account.
-- Note: Uniqueness is (tenant_scope, provider, subject), modelled directly on kyc_identity's (tenant_scope, person_key). There is deliberately NO unique index on (provider, subject) alone: one Google account legitimately backs DIFFERENT avatars on different consumers, and a node-global binding would collapse those into one identity — the exact cross-tenant failure D1/D23 exist to prevent.
-- Note: This table NEVER satisfies KYC. It proves control of a provider account; kyc_identity proves a person. The value gate (Services/Kyc/KycGateService) reads kyc_submission + kyc_identity ONLY and has no read path to this table, so an OAuth-only avatar can never pass it.
-- Note: Deleting a row unlinks a provider and MUST NOT cascade to the avatar: the avatar row is independent and stays reachable through its other credentials (password, wallet, or another provider). ExternalLoginService refuses to unlink the last remaining credential rather than orphaning the account.
-- Note: email_at_link/display_name_at_link are AUDIT-ONLY snapshots of what the provider asserted at link time. Nothing reads them to resolve an account; the lookup key is (tenant_scope, provider, subject).
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- AvatarExternalLogin.ExternalAuthProvider
DEFINE PARAM IF NOT EXISTS $avatar_external_login_provider VALUE ["GOOGLE", "GITHUB"];

DEFINE TABLE IF NOT EXISTS avatar_external_login SCHEMAFULL;

-- Deterministic record id (SHA-256 of tenant_scope + provider + subject)
DEFINE FIELD IF NOT EXISTS id ON TABLE avatar_external_login TYPE string
    ASSERT $value != NONE AND $value != "";

-- Uniqueness scope: per-tenant, mirroring kyc_identity (D23)
DEFINE FIELD IF NOT EXISTS tenant_scope ON TABLE avatar_external_login TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE avatar_external_login TYPE option<record<avatar>>;

-- Federated identity (provider's stable id — NEVER the email)
DEFINE FIELD IF NOT EXISTS provider ON TABLE avatar_external_login TYPE string
    ASSERT $value INSIDE $avatar_external_login_provider;
DEFINE FIELD IF NOT EXISTS subject ON TABLE avatar_external_login TYPE string
    ASSERT $value != NONE AND $value != "";

-- Bound avatar (unlink deletes THIS row only, never the avatar)
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE avatar_external_login TYPE record<avatar>;

-- Audit-only provider assertions (never a lookup key)
DEFINE FIELD IF NOT EXISTS email_at_link ON TABLE avatar_external_login TYPE option<string>;
DEFINE FIELD IF NOT EXISTS display_name_at_link ON TABLE avatar_external_login TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS linked_at ON TABLE avatar_external_login TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS last_login_at ON TABLE avatar_external_login TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS avatar_external_login_scope_provider_subject
    ON TABLE avatar_external_login
    FIELDS tenant_scope, provider, subject
    UNIQUE;

DEFINE INDEX IF NOT EXISTS avatar_external_login_by_avatar
    ON TABLE avatar_external_login
    FIELDS avatar_id;
