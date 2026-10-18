-- ============================================================
-- Table: user
-- Aggregate: User (Models/User.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.user_id FK.
-- Note: Tenant ownership (tenant-onboarding): owner_tenant_id is a self-FK to the tenant principal's user; external_user_id is the tenant's own user id for this child. The user_tenant_extuser composite UNIQUE is only meaningful for rows where BOTH owner_tenant_id AND external_user_id are non-NONE. A UNIQUE index does NOT collide NONE components -- verified empirically against SurrealDB 3.1.4, and explicit SQL NULL is unreachable under SCHEMAFULL option<T> -- so legacy/self-registered users that leave both NONE coexist freely and never contend. A tenant-managed row ALWAYS sets both (TenantManager.ProvisionChildAsync), so dedup-per-tenant is correct by construction. Earlier wording here inherited ApiKey.cs's 'NULL-equals-NULL collision' phrasing for a hazard that does not exist in this schema. See tests/AZOA.WebAPI.Tests/Kyc/SurrealUniqueIndexNoneSemanticsTests.cs.
-- ============================================================

DEFINE TABLE IF NOT EXISTS user SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE user TYPE string;
DEFINE FIELD IF NOT EXISTS username ON TABLE user TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS email ON TABLE user TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS password_hash ON TABLE user TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS title ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS first_name ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS last_name ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE user TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS last_beamed_in_date ON TABLE user TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE user TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS is_verified ON TABLE user TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS dapp_role ON TABLE user TYPE string
    ASSERT $value != NONE AND $value != ""
    DEFAULT "dapp:user";
DEFINE FIELD IF NOT EXISTS owner_tenant_id ON TABLE user TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS external_user_id ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS external_ref ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_wallet_address ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_wallet_chain_type ON TABLE user TYPE option<string>;
DEFINE FIELD IF NOT EXISTS auth_identity_version ON TABLE user TYPE option<int>;
DEFINE FIELD IF NOT EXISTS auth_not_before ON TABLE user TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS discoverable ON TABLE user TYPE bool
    DEFAULT ALWAYS false;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS user_username
    ON TABLE user
    FIELDS username
    UNIQUE;

DEFINE INDEX IF NOT EXISTS user_email
    ON TABLE user
    FIELDS email
    UNIQUE;

DEFINE INDEX IF NOT EXISTS user_owner_tenant
    ON TABLE user
    FIELDS owner_tenant_id;

DEFINE INDEX IF NOT EXISTS user_tenant_extuser
    ON TABLE user
    FIELDS owner_tenant_id, external_user_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS user_auth_wallet
    ON TABLE user
    FIELDS auth_wallet_address, auth_wallet_chain_type
    UNIQUE;
