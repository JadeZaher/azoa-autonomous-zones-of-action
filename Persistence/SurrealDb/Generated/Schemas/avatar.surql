-- ============================================================
-- Table: avatar
-- Aggregate: Avatar (Models/Avatar.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.avatar_id FK.
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
DEFINE FIELD IF NOT EXISTS created_date ON TABLE avatar TYPE datetime;
DEFINE FIELD IF NOT EXISTS last_beamed_in_date ON TABLE avatar TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE avatar TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS is_verified ON TABLE avatar TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS karma ON TABLE avatar TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS level ON TABLE avatar TYPE int
    DEFAULT 1;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS avatar_username
    ON TABLE avatar
    FIELDS username
    UNIQUE;

DEFINE INDEX IF NOT EXISTS avatar_email
    ON TABLE avatar
    FIELDS email
    UNIQUE;
