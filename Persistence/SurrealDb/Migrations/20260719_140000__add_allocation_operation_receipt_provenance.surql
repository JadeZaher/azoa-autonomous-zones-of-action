-- Preserve the shipped operation-log baseline while adding allocation receipt provenance.
DEFINE FIELD OVERWRITE idempotency_key ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9a-f]{64}$");
DEFINE FIELD IF NOT EXISTS initiator_avatar_id ON TABLE operation_log TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS initiator_api_key_id ON TABLE operation_log TYPE option<record<api_key>>;
