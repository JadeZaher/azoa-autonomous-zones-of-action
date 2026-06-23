-- ============================================================
-- Table: wallet_auth_claim_token
-- Aggregate: WalletAuthClaimToken (Models/WalletAuthClaimToken.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Tenant-minted single-use, short-TTL claim-invite token (AC4). A tenant generates it for a child it owns; the user redeems it with a USER-SIDE credential (fresh wallet challenge OR user-chosen password). The token alone NEVER sets a credential -- it only authorizes the credential-setting step (M1).
-- Note: Same anti-TOCTOU rule as the auth nonce: token is UNIQUE and consumed atomically (consumed_at = NONE AND expires_at > now) at a winning claim. target_avatar_id is the child being claimed; tenant_id is the minting tenant (asserted owner at mint time). Cross-tenant redemption resolves to NotFound (isolation crux).
-- ============================================================

DEFINE TABLE IF NOT EXISTS wallet_auth_claim_token SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet_auth_claim_token TYPE string;
DEFINE FIELD IF NOT EXISTS token ON TABLE wallet_auth_claim_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS target_avatar_id ON TABLE wallet_auth_claim_token TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE wallet_auth_claim_token TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE wallet_auth_claim_token TYPE datetime;
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE wallet_auth_claim_token TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wallet_auth_claim_token TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wallet_auth_claim_token_token
    ON TABLE wallet_auth_claim_token
    FIELDS token
    UNIQUE;

DEFINE INDEX IF NOT EXISTS wallet_auth_claim_token_by_target
    ON TABLE wallet_auth_claim_token
    FIELDS target_avatar_id;
