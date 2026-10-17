-- ============================================================
-- Table: exchange_hold_settlement_receipt
-- Aggregate: Exchange finalized accounting
-- Guardrail: Exact source, wire and custody projection commit once
-- ============================================================

DEFINE TABLE IF NOT EXISTS exchange_hold_settlement_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS source_intent_hash ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS evidence_json ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS treasury_record_id ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS reserve_record_id ON TABLE exchange_hold_settlement_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS movement_json ON TABLE exchange_hold_settlement_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE exchange_hold_settlement_receipt TYPE datetime;
