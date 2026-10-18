-- Old-row rule (round-4 W1): fields added to tables that predate round 1 carry DEFAULT ALWAYS so
-- an old row stays writable, but CAS predicates such as `version = $expected` compare the stored
-- NONE, not the default. Materialize the defaults once so preserved rows join the CAS protocol.
-- A no-op on a fresh database. See ../AGENTS.md §old-row-rule.
UPDATE action_node_execution SET version = version ?? 0, manual_advance_released = manual_advance_released ?? false
    WHERE version = NONE OR manual_advance_released = NONE;
UPDATE action_run SET version = version ?? 0 WHERE version = NONE;
UPDATE consent_grant SET version = version ?? 0 WHERE version = NONE;
UPDATE action_webhook_event SET version = version ?? 0, retry_generation = retry_generation ?? 0,
    cycle_attempt_count = cycle_attempt_count ?? 0
    WHERE version = NONE OR retry_generation = NONE OR cycle_attempt_count = NONE;
UPDATE consent_webhook_event SET version = version ?? 0, retry_generation = retry_generation ?? 0,
    cycle_attempt_count = cycle_attempt_count ?? 0
    WHERE version = NONE OR retry_generation = NONE OR cycle_attempt_count = NONE;
UPDATE idempotency_key_store SET preparation_generation = preparation_generation ?? 0
    WHERE preparation_generation = NONE;
UPDATE fiat_payout SET bank_payout_attempt = bank_payout_attempt ?? 0, environment = environment ?? "unbound",
    chain_network = chain_network ?? "unbound", recovery_requested = recovery_requested ?? false,
    recovery_reversal_amount = recovery_reversal_amount ?? 0,
    restored_quantity_base_units = restored_quantity_base_units ?? "0",
    restitution_target_base_units = restitution_target_base_units ?? "0",
    recovery_audit_json = recovery_audit_json ?? "[]", recovery_reversal_attempt = recovery_reversal_attempt ?? 0
    WHERE bank_payout_attempt = NONE OR environment = NONE OR chain_network = NONE OR recovery_requested = NONE
        OR recovery_reversal_amount = NONE OR restored_quantity_base_units = NONE
        OR restitution_target_base_units = NONE OR recovery_audit_json = NONE OR recovery_reversal_attempt = NONE;
UPDATE fiat_reversal SET purchase_recovered_base_units = purchase_recovered_base_units ?? "0"
    WHERE purchase_recovered_base_units = NONE;
UPDATE on_ramp_lot SET recovery_audit_json = recovery_audit_json ?? "[]", environment = environment ?? "unbound",
    chain_network = chain_network ?? "unbound", refund_attempt = refund_attempt ?? 0,
    refunded_minor_units = refunded_minor_units ?? 0, reversed_base_units = reversed_base_units ?? "0",
    reversal_marks_json = reversal_marks_json ?? "{}"
    WHERE recovery_audit_json = NONE OR environment = NONE OR chain_network = NONE OR refund_attempt = NONE
        OR refunded_minor_units = NONE OR reversed_base_units = NONE OR reversal_marks_json = NONE;
UPDATE webhook_registration SET encrypted_secret = encrypted_secret ?? "", version = version ?? 0,
    destination_revision = destination_revision ?? 0, secret_revision = secret_revision ?? 0
    WHERE encrypted_secret = NONE OR version = NONE OR destination_revision = NONE OR secret_revision = NONE;
