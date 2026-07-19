-- Preserve the shipped holon checksum while adding durable transfer claims.
DEFINE FIELD IF NOT EXISTS transfer_reservation_key ON TABLE holon TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transfer_target_avatar_id ON TABLE holon TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS transfer_reserved_at ON TABLE holon TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS last_transfer_settlement_key ON TABLE holon TYPE option<string>;
DEFINE INDEX IF NOT EXISTS holon_transfer_reservation
    ON TABLE holon
    FIELDS transfer_reservation_key
    UNIQUE;
