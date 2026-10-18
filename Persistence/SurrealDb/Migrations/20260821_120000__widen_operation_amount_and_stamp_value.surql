-- D13 + D16 on operation_log.
--
-- D13: amount moves from a 64-bit integer to a decimal base-unit STRING. The domain type
-- is now RailAmount (BigInteger base units), and SurrealDB's `int` is i64 — an ordinary
-- wei-denominated Ethereum amount does not fit, so leaving the column numeric would
-- re-impose the ceiling the widening removed one layer down.
--
-- D16: five columns carry the fiat value stamped at settlement. All five NONE means the
-- operation was never priced; a value_rate of "0" means a rate source really quoted zero.
-- Those are different facts and stay distinguishable. See
-- Persistence/SurrealDb/Models/AGENTS.md §value-stamp.

-- Step 1 of 3. Widen to a union first. Redefining straight to option<string> would leave
-- every already-stored i64 amount in the table un-coercible on the next read.
DEFINE FIELD OVERWRITE amount ON TABLE operation_log TYPE option<int | string>;

-- Step 2 of 3. Rewrite the stored numeric amounts into the canonical decimal string.
-- Guarded by type::is_number so re-running the migration is a no-op on rows already
-- rewritten (the runner is forward-only, but a partially applied batch must be safe).
UPDATE operation_log
    SET amount = <string> amount
    WHERE amount != NONE AND type::is_number(amount);

-- Step 3 of 3. Pin the final type and reject anything that is not an unsigned decimal
-- count — no sign, no separators, no exponent, matching RailAmount.TryParse.
DEFINE FIELD OVERWRITE amount ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+$");

-- D16 value stamp. Every existing row is legitimately unstamped: nothing implements
-- IValueRateSource yet, and these MUST NOT be backfilled from a later price — the point
-- of storing the rate and its timestamp is that the figure stays derivable from what was
-- actually known at settlement.
DEFINE FIELD IF NOT EXISTS value_currency_code ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[A-Z]{3}$");
DEFINE FIELD IF NOT EXISTS value_minor_units ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+$");
DEFINE FIELD IF NOT EXISTS value_rate ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+(\\.[0-9]+)?$");
DEFINE FIELD IF NOT EXISTS value_rate_as_of ON TABLE operation_log TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS value_rate_source ON TABLE operation_log TYPE option<string>;
