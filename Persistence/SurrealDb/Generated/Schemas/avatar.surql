-- ============================================================
-- Table: avatar
-- Aggregate: Avatar (Models/Avatar.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.avatar_id FK.
-- Note: Tenant ownership (tenant-onboarding): owner_tenant_id is a self-FK to the tenant principal's avatar; external_user_id is the tenant's own user id for this child. The avatar_tenant_extuser composite UNIQUE is only meaningful for rows where BOTH owner_tenant_id AND external_user_id are non-NONE — same NULL-equals-NULL collision caveat the ApiKey POCO documents (ApiKey.cs:17). A tenant-managed row ALWAYS sets both (TenantManager.ProvisionChildAsync), so dedup-per-tenant is correct by construction; legacy/self-registered avatars leave both NONE and never collide.
-- ============================================================

DEFINE TABLE IF NOT EXISTS avatar SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE avatar TYPE string;
DEFINE FIELD IF NOT EXISTS username ON TABLE avatar TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS email ON TABLE avatar TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS password_hash ON TABLE avatar TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS title ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS first_name ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS last_name ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE avatar TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS last_beamed_in_date ON TABLE avatar TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE avatar TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS is_verified ON TABLE avatar TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS owner_tenant_id ON TABLE avatar TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS external_user_id ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS external_ref ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_wallet_address ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_wallet_chain_type ON TABLE avatar TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_not_before ON TABLE avatar TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS avatar_username
    ON TABLE avatar
    FIELDS username
    UNIQUE;

DEFINE INDEX IF NOT EXISTS avatar_email
    ON TABLE avatar
    FIELDS email
    UNIQUE;

DEFINE INDEX IF NOT EXISTS avatar_owner_tenant
    ON TABLE avatar
    FIELDS owner_tenant_id;

DEFINE INDEX IF NOT EXISTS avatar_tenant_extuser
    ON TABLE avatar
    FIELDS owner_tenant_id, external_user_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS avatar_auth_wallet
    ON TABLE avatar
    FIELDS auth_wallet_address, auth_wallet_chain_type
    UNIQUE;
