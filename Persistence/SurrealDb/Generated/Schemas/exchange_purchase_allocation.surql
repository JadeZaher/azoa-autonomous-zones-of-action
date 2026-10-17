-- ============================================================
-- Table: exchange_purchase_allocation
-- Aggregate: Fiat reversal purchase attribution
-- Guardrail: One immutable lot-to-purchase identity whose recovered share only grows atomically
-- ============================================================

DEFINE TABLE IF NOT EXISTS exchange_purchase_allocation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS lot_id ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS source_id ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS consumed_source_units ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS recovered_hold_units ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS clawed_asset_units ON TABLE exchange_purchase_allocation TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE exchange_purchase_allocation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE exchange_purchase_allocation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE exchange_purchase_allocation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS exchange_purchase_allocation_source
    ON TABLE exchange_purchase_allocation
    FIELDS source_id;
