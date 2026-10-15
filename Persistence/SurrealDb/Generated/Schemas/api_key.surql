-- ============================================================
-- Table: api_key
-- Aggregate: ApiKey (Models/ApiKey.cs)
-- Guardrail: G6 SCHEMAFULL, G2 UNIQUE-on-key_hash insert-wins lookup
-- Note: Authentication ledger. The handler hashes the inbound X-Api-Key header (SHA-256) and looks up key_hash via the unique index. The raw key never reaches storage. key_prefix is the first 16 chars of the raw key for caller display only.
-- Note: B3 nullable-collision review: api_key_unique_hash UNIQUE on key_hash is NOT NULL (ASSERT != NONE AND != ""), so the question of how the index treats absent values never arises here -- every row carries a real hash and dedup is correct by construction. For the record, verified empirically against SurrealDB 3.1.4: a UNIQUE index does NOT collide NONE components (two rows differing only by a NONE both CREATE), and explicit SQL NULL is unreachable under SCHEMAFULL option<T>, which refuses to coerce it. Earlier wording here invoked 'NULL-equals-NULL collision semantics'; that hazard does not exist in this schema. bridge_tx.surql states it correctly. See tests/AZOA.WebAPI.Tests/Kyc/SurrealUniqueIndexNoneSemanticsTests.cs.
-- Note: Soft-delete via revoked_at + is_active. Handlers MUST check both is_active AND revoked_at IS NONE AND (expires_at IS NONE OR expires_at > now) before granting authentication. last_used_at is updated fire-and-forget from the handler -- TouchLastUsedAsync must never throw.
-- ============================================================

DEFINE TABLE IF NOT EXISTS api_key SCHEMAFULL;

-- Core identity (record id is the Guid('N') of ApiKey.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE api_key TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owner user (Guid('N') hex); indexed for ListByUser
DEFINE FIELD IF NOT EXISTS user_id ON TABLE api_key TYPE record<user>;

-- Caller-supplied label (free string)
DEFINE FIELD IF NOT EXISTS name ON TABLE api_key TYPE string;

-- SHA-256(raw key) hex -- the dedup key; UNIQUE
DEFINE FIELD IF NOT EXISTS key_hash ON TABLE api_key TYPE string
    ASSERT $value != NONE AND $value != "";

-- Display prefix (first 16 chars of raw key, never the secret)
DEFINE FIELD IF NOT EXISTS key_prefix ON TABLE api_key TYPE string;

-- Creation timestamp (UTC)
DEFINE FIELD IF NOT EXISTS created_date ON TABLE api_key TYPE datetime
    READONLY;

-- Optional expiry (NONE = no expiry)
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE api_key TYPE option<datetime>;

-- Last-used timestamp (fire-and-forget; may lag under load)
DEFINE FIELD IF NOT EXISTS last_used_at ON TABLE api_key TYPE option<datetime>;

-- Soft-delete marker (NONE = active)
DEFINE FIELD IF NOT EXISTS revoked_at ON TABLE api_key TYPE option<datetime>;

-- Active flag mirror for cheap WHERE filtering
DEFINE FIELD IF NOT EXISTS is_active ON TABLE api_key TYPE bool
    DEFAULT true;

-- Scope tokens (empty array = full access)
DEFINE FIELD IF NOT EXISTS scopes ON TABLE api_key TYPE array<string>
    DEFAULT [];

-- Allowed origins (empty array = all allowed)
DEFINE FIELD IF NOT EXISTS allowed_origins ON TABLE api_key TYPE array<string>
    DEFAULT [];

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS api_key_unique_hash
    ON TABLE api_key
    FIELDS key_hash
    UNIQUE;

DEFINE INDEX IF NOT EXISTS api_key_by_user
    ON TABLE api_key
    FIELDS user_id;
