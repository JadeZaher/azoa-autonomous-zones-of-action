-- Preserve shipped operation checksums while recording submitted chain work.
DEFINE PARAM OVERWRITE $operation_log_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"];
DEFINE FIELD OVERWRITE status ON TABLE operation_log TYPE string
    ASSERT $value INSIDE $operation_log_status
    DEFAULT "Pending";

DEFINE PARAM OVERWRITE $swap_state_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"];
DEFINE FIELD OVERWRITE status ON TABLE swap_state TYPE string
    ASSERT $value INSIDE $swap_state_status
    DEFAULT "Pending";
