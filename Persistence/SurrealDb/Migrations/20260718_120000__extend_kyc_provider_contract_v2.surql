-- Expand the KYC ledger for tenant-selectable hosted providers without editing
-- the immutable v1 schema checksum. Existing rows are backfilled before the
-- new fields become required.

DEFINE PARAM OVERWRITE $kyc_submission_provider VALUE ["MANUAL", "VERIFF", "GENERIC_HOSTED"];

DEFINE FIELD IF NOT EXISTS provider_key ON TABLE kyc_submission TYPE option<string>;
DEFINE FIELD IF NOT EXISTS hosted_verification ON TABLE kyc_submission TYPE option<bool>;
DEFINE FIELD IF NOT EXISTS accepts_document_references ON TABLE kyc_submission TYPE option<bool>;
DEFINE FIELD IF NOT EXISTS verification_url ON TABLE kyc_submission TYPE option<string>;
DEFINE FIELD IF NOT EXISTS session_instructions ON TABLE kyc_submission TYPE option<string>;

UPDATE kyc_submission
SET provider_key = "manual"
WHERE provider = "MANUAL"
  AND (provider_key = NONE OR provider_key = "");

UPDATE kyc_submission
SET provider_key = "veriff"
WHERE provider = "VERIFF"
  AND (provider_key = NONE OR provider_key = "");

UPDATE kyc_submission
SET provider_key = "hosted"
WHERE provider = "GENERIC_HOSTED"
  AND (provider_key = NONE OR provider_key = "");

UPDATE kyc_submission SET hosted_verification = false
WHERE provider = "MANUAL" AND hosted_verification = NONE;
UPDATE kyc_submission SET hosted_verification = true
WHERE provider IN ["VERIFF", "GENERIC_HOSTED"] AND hosted_verification = NONE;

UPDATE kyc_submission SET accepts_document_references = true
WHERE provider = "MANUAL" AND accepts_document_references = NONE;
UPDATE kyc_submission SET accepts_document_references = false
WHERE provider IN ["VERIFF", "GENERIC_HOSTED"] AND accepts_document_references = NONE;

DEFINE FIELD OVERWRITE provider_key ON TABLE kyc_submission TYPE string;
DEFINE FIELD OVERWRITE hosted_verification ON TABLE kyc_submission TYPE bool;
DEFINE FIELD OVERWRITE accepts_document_references ON TABLE kyc_submission TYPE bool;
