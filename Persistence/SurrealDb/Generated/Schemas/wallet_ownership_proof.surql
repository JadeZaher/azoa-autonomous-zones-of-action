-- ============================================================
-- Table: wallet_ownership_proof
-- Aggregate: WalletOwnershipProof (Models/WalletOwnershipProof.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Record id = sha256(chain:canonical address): one current prover per address; a newer verified proof supersedes. Written only by the wallet ownership-proof route after a consumed wallet-auth challenge verifies.
-- ============================================================

DEFINE TABLE IF NOT EXISTS wallet_ownership_proof SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet_ownership_proof TYPE string;
DEFINE FIELD IF NOT EXISTS user_id ON TABLE wallet_ownership_proof TYPE record<user>;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE wallet_ownership_proof TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS address ON TABLE wallet_ownership_proof TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS wallet_id ON TABLE wallet_ownership_proof TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS proven_at ON TABLE wallet_ownership_proof TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wallet_ownership_proof_user
    ON TABLE wallet_ownership_proof
    FIELDS user_id;
