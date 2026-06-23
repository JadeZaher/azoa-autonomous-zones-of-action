-- ============================================================
-- Table: wallet_auth_challenge
-- Aggregate: WalletAuthChallenge (Models/WalletAuthChallenge.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Single-use wallet-challenge auth nonce (AC1/AC1b). The user signs the EXACT domain_message bytes; verify re-validates every field server-side then atomically consumes via a conditional UPDATE (consumed_at = NONE AND expires_at > now). nonce is UNIQUE -- the no-TOCTOU single-use primitive: two concurrent verifies of one nonce yield exactly one success.
-- Note: domain_message is the literal human-readable bytes-to-sign (AZOA-AUTH-v1 prefix + issuer/audience + chain_type + address + nonce + expiry). Stored verbatim so the server is the single source of truth for the signed payload; verify never trusts a client-supplied message except as an exact-equality cross-check against this column.
-- Note: Bound to (address, chain_type); the by-address index serves the latest-live lookup. consumed_at = NONE means unconsumed; a row is dead once consumed_at != NONE OR expires_at <= now. Greenfield: expired/consumed rows may be reaped by a future janitor -- not required for correctness given the atomic consume.
-- ============================================================

DEFINE TABLE IF NOT EXISTS wallet_auth_challenge SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet_auth_challenge TYPE string;
DEFINE FIELD IF NOT EXISTS nonce ON TABLE wallet_auth_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS address ON TABLE wallet_auth_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE wallet_auth_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS domain_message ON TABLE wallet_auth_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE wallet_auth_challenge TYPE datetime;
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE wallet_auth_challenge TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wallet_auth_challenge TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wallet_auth_challenge_nonce
    ON TABLE wallet_auth_challenge
    FIELDS nonce
    UNIQUE;

DEFINE INDEX IF NOT EXISTS wallet_auth_challenge_by_address
    ON TABLE wallet_auth_challenge
    FIELDS address, chain_type;
