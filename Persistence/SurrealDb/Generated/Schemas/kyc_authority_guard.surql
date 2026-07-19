-- Serialization records for KYC authority mutation and active-attempt admission.
DEFINE TABLE IF NOT EXISTS kyc_authority_guard SCHEMAFULL;
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_authority_guard TYPE string ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS kind ON TABLE kyc_authority_guard TYPE string ASSERT $value INSIDE ["provider", "tenant", "attempt"];
DEFINE FIELD IF NOT EXISTS authority_revision ON TABLE kyc_authority_guard TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE kyc_authority_guard TYPE option<string>;
DEFINE FIELD IF NOT EXISTS touched_at ON TABLE kyc_authority_guard TYPE datetime DEFAULT time::now();
