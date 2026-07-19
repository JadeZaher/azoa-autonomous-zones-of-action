-- Preserve the original admin bootstrap checksum while adding a monotonic
-- credential epoch for dedicated node-operator sessions.

DEFINE FIELD IF NOT EXISTS credential_revision ON TABLE admin_bootstrap_state TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS session_revision ON TABLE admin_bootstrap_state TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS credential_updated_at ON TABLE admin_bootstrap_state TYPE option<datetime>;
