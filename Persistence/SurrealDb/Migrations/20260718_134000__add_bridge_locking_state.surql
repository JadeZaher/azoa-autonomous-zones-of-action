-- Add a durable before-effect state for trusted bridge source locks.
DEFINE PARAM OVERWRITE $bridge_tx_status VALUE ["Initiated", "Locking", "Locked", "AwaitingVAA", "VAAReady", "Redeeming", "Completed", "Failed", "Refunded", "Reversing"];
DEFINE FIELD OVERWRITE status ON TABLE bridge_tx TYPE string
    ASSERT $value INSIDE $bridge_tx_status;
