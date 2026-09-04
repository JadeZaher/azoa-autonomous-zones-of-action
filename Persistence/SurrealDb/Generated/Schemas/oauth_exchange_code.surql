-- ============================================================
-- Table: oauth_exchange_code
-- Aggregate: OAuthExchangeCode (single-use handoff between the OAuth redirect and the SPA)
-- Guardrail: G6 SCHEMAFULL
-- Note: Two purposes, one primitive. SESSION: the provider callback cannot hand a JWT to a single-page app without putting it in a URL (browser history, Referer, server logs), so the callback redirect carries a short-lived opaque code the SPA POSTs back for the real token. LINK: proves an ALREADY-AUTHENTICATED avatar explicitly asked to attach a provider, because a top-level OAuth redirect carries no Authorization header.
-- Note: Stores the SHA-256 of the code, never the code itself, exactly as ApiKey stores a hashed key: a database read must not yield a usable credential.
-- Note: Same anti-TOCTOU rule as wallet_auth_challenge: code_hash is UNIQUE and consumed atomically (consumed_at = NONE AND expires_at > now) so exactly one caller observes AffectedCount() == 1. Replaying a code returns NotFound, never a second session.
-- Note: tenant_scope is carried through the redirect so the exchange resolves the binding in the SAME scope the start call used; a code minted under one tenant can never mint a session in another.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- OAuthExchangeCode.OAuthExchangeCodePurpose
DEFINE PARAM IF NOT EXISTS $oauth_exchange_code_purpose VALUE ["SESSION", "LINK"];
-- OAuthExchangeCode.ExternalAuthProvider
DEFINE PARAM IF NOT EXISTS $oauth_exchange_code_provider VALUE ["GOOGLE", "GITHUB"];

DEFINE TABLE IF NOT EXISTS oauth_exchange_code SCHEMAFULL;

-- Record id (Guid('N') of the minting)
DEFINE FIELD IF NOT EXISTS id ON TABLE oauth_exchange_code TYPE string
    ASSERT $value != NONE AND $value != "";

-- Hashed single-use credential
DEFINE FIELD IF NOT EXISTS code_hash ON TABLE oauth_exchange_code TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS purpose ON TABLE oauth_exchange_code TYPE string
    ASSERT $value INSIDE $oauth_exchange_code_purpose;

-- Subject + scope
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE oauth_exchange_code TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS provider ON TABLE oauth_exchange_code TYPE string
    ASSERT $value INSIDE $oauth_exchange_code_provider;
DEFINE FIELD IF NOT EXISTS tenant_scope ON TABLE oauth_exchange_code TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE oauth_exchange_code TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS return_to ON TABLE oauth_exchange_code TYPE option<string>;

-- Single-use lifecycle
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE oauth_exchange_code TYPE datetime;
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE oauth_exchange_code TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE oauth_exchange_code TYPE datetime
    DEFAULT time::now()
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS oauth_exchange_code_hash
    ON TABLE oauth_exchange_code
    FIELDS code_hash
    UNIQUE;

DEFINE INDEX IF NOT EXISTS oauth_exchange_code_expiry
    ON TABLE oauth_exchange_code
    FIELDS expires_at;
