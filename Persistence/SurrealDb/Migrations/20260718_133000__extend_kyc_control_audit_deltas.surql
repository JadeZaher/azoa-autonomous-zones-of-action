-- Non-secret before/after values make profile metadata audits actionable.
DEFINE FIELD IF NOT EXISTS previous_display_name ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS display_name ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS previous_adapter_key ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS adapter_key ON TABLE kyc_control_audit TYPE option<string>;
