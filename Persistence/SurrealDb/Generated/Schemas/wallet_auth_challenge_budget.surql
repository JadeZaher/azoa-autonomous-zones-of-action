-- ============================================================
-- Table: wallet_auth_challenge_budget
-- ============================================================

DEFINE TABLE IF NOT EXISTS wallet_auth_challenge_budget SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet_auth_challenge_budget TYPE string;
DEFINE FIELD IF NOT EXISTS slots_json ON TABLE wallet_auth_challenge_budget TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE wallet_auth_challenge_budget TYPE int;
