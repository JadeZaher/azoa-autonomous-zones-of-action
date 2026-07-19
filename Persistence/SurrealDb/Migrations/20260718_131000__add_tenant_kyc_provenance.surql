-- Bind every new tenant KYC attempt to the selected provider revision so a
-- later selection change cannot inherit stale approval authority.

DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE kyc_submission TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS provider_selection_version ON TABLE kyc_submission TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS provider_trust_revision ON TABLE kyc_submission TYPE int DEFAULT 0;
DEFINE INDEX IF NOT EXISTS kyc_submission_tenant_id ON TABLE kyc_submission FIELDS tenant_id;
