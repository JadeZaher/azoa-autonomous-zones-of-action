-- ============================================================
-- Table: consent_grant
-- Aggregate: ConsentGrant (Models/ConsentGrant.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: A user-granted, revocable authorization for a tenant to drive actions within Scopes. The signing seam does a LIVE validity check against this row before every tenant-driven key decrypt (AC4/AC5). revoked_at = NONE AND (expires_at = NONE OR now < expires_at) is the live predicate.
-- Note: Scopes stored as a CSV string (mirrors api_key.scopes). origin = UserExplicit|Participation. participation_ref is the opaque ArdaNova id; offboard revokes by (tenant_id, participation_ref) exact-match within the tenant's own grants only (L3).
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $consent_grant_origin VALUE ["UserExplicit", "Participation"];

DEFINE TABLE IF NOT EXISTS consent_grant SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_grant TYPE string;
DEFINE FIELD IF NOT EXISTS grantor_avatar_id ON TABLE consent_grant TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_grant TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS scopes ON TABLE consent_grant TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS origin ON TABLE consent_grant TYPE string
    ASSERT $value INSIDE $consent_grant_origin
    DEFAULT "UserExplicit";
DEFINE FIELD IF NOT EXISTS participation_ref ON TABLE consent_grant TYPE option<string>;
DEFINE FIELD IF NOT EXISTS granted_at ON TABLE consent_grant TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE consent_grant TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS revoked_at ON TABLE consent_grant TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consent_grant_by_grantor
    ON TABLE consent_grant
    FIELDS grantor_avatar_id;

DEFINE INDEX IF NOT EXISTS consent_grant_by_tenant
    ON TABLE consent_grant
    FIELDS tenant_id;

DEFINE INDEX IF NOT EXISTS consent_grant_seam_lookup
    ON TABLE consent_grant
    FIELDS grantor_avatar_id, tenant_id;

DEFINE INDEX IF NOT EXISTS consent_grant_tenant_partref
    ON TABLE consent_grant
    FIELDS tenant_id, participation_ref;
