-- ============================================================
-- Table: node_fee_schedule
-- Aggregate: NodeFeeSchedule (Persistence/SurrealDb/Models/NodeFeeSchedule.cs)
-- Guardrail: G6 SCHEMAFULL; node-local fee schedule
-- Note: Singleton local fee policy. Flat values are non-negative integer base-unit strings; bps values are 0..10000. Allocation Mint settles fees by netting the minted amount; nonzero Transfer fees remain fail-closed pending on-chain treasury settlement. Fees never create an off-chain balance.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_fee_schedule SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_fee_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS mint_flat_base_units ON TABLE node_fee_schedule TYPE string
    ASSERT string::matches($value, "^[0-9]+$")
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS mint_bps ON TABLE node_fee_schedule TYPE int
    ASSERT $value >= 0 AND $value <= 10000
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS transfer_flat_base_units ON TABLE node_fee_schedule TYPE string
    ASSERT string::matches($value, "^[0-9]+$")
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS transfer_bps ON TABLE node_fee_schedule TYPE int
    ASSERT $value >= 0 AND $value <= 10000
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS swap_flat_base_units ON TABLE node_fee_schedule TYPE string
    ASSERT string::matches($value, "^[0-9]+$")
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS swap_bps ON TABLE node_fee_schedule TYPE int
    ASSERT $value >= 0 AND $value <= 10000
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS quest_complete_flat_base_units ON TABLE node_fee_schedule TYPE string
    ASSERT string::matches($value, "^[0-9]+$")
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS quest_complete_bps ON TABLE node_fee_schedule TYPE int
    ASSERT $value >= 0 AND $value <= 10000
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS federation_publish_flat_base_units ON TABLE node_fee_schedule TYPE string
    ASSERT string::matches($value, "^[0-9]+$")
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS federation_publish_bps ON TABLE node_fee_schedule TYPE int
    ASSERT $value >= 0 AND $value <= 10000
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS version ON TABLE node_fee_schedule TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS updated_by_avatar_id ON TABLE node_fee_schedule TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_fee_schedule TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_fee_schedule TYPE datetime;
