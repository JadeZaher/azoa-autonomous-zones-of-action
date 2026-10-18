-- D-3 (round 4, L5): client-signed transfers park in NeedsOperator when only an operator can settle them.
-- operation_log is baselined, so the generated [Inside] list reaches deployed databases only through this overwrite.
DEFINE PARAM OVERWRITE $operation_log_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called", "NeedsOperator"];
