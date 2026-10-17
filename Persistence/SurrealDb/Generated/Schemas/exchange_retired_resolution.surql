-- ============================================================
-- Table: exchange_retired_resolution
-- Aggregate: Exchange custody source
-- Guardrail: G6 SCHEMAFULL; record id is the operation id, so one create-only resolution per retired exchange row
-- Note: Generic reconciliation completes or fails a retired exchange row only when chain truth agrees with this attestation; see Services/Reserve/AGENTS.md §retired-rows.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $exchange_retired_resolution_outcome VALUE ["Confirmed", "Failed"];

DEFINE TABLE IF NOT EXISTS exchange_retired_resolution SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE exchange_retired_resolution TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE exchange_retired_resolution TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS outcome ON TABLE exchange_retired_resolution TYPE string
    ASSERT $value INSIDE $exchange_retired_resolution_outcome;
DEFINE FIELD IF NOT EXISTS transaction_hash ON TABLE exchange_retired_resolution TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS reason ON TABLE exchange_retired_resolution TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS resolved_by_user_id ON TABLE exchange_retired_resolution TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS created_at ON TABLE exchange_retired_resolution TYPE datetime;
