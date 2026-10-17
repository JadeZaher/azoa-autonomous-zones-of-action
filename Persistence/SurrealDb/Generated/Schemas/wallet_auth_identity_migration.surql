-- ============================================================
-- Table: wallet_auth_identity_migration
-- ============================================================

DEFINE TABLE IF NOT EXISTS wallet_auth_identity_migration SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet_auth_identity_migration TYPE string;
DEFINE FIELD IF NOT EXISTS status ON TABLE wallet_auth_identity_migration TYPE string;
DEFINE FIELD IF NOT EXISTS lease_token ON TABLE wallet_auth_identity_migration TYPE option<string>;
DEFINE FIELD IF NOT EXISTS lease_expires_at ON TABLE wallet_auth_identity_migration TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS diagnostics_json ON TABLE wallet_auth_identity_migration TYPE string;
DEFINE FIELD IF NOT EXISTS audit_json ON TABLE wallet_auth_identity_migration TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE wallet_auth_identity_migration TYPE int;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wallet_auth_identity_migration TYPE datetime;
