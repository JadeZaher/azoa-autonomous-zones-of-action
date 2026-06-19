-- ============================================================
-- Table: kyc_document
-- Aggregate: KycDocument (generic identity-verification module)
-- Guardrail: G6 SCHEMAFULL
-- Note: Document record attached to a kyc_submission. file_url references external blob storage (out of scope for this module); only the metadata + URL are persisted here. type persists as the string enum name.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- KycDocument.KycDocumentType
DEFINE PARAM IF NOT EXISTS $kyc_document_type VALUE ["GOVERNMENT_ID", "PASSPORT", "DRIVERS_LICENSE", "SELFIE", "PROOF_OF_ADDRESS"];

DEFINE TABLE IF NOT EXISTS kyc_document SCHEMAFULL;

-- Core identity (record id is the Guid('N') of the document)
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_document TYPE string
    ASSERT $value != NONE AND $value != "";

-- Parent submission (Guid('N') hex record link); indexed for per-submission listing
DEFINE FIELD IF NOT EXISTS submission_id ON TABLE kyc_document TYPE option<record<kyc_submission>>;

-- Document classification
DEFINE FIELD IF NOT EXISTS type ON TABLE kyc_document TYPE string
    ASSERT $value INSIDE $kyc_document_type;

-- Blob reference + display metadata
DEFINE FIELD IF NOT EXISTS file_url ON TABLE kyc_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS file_name ON TABLE kyc_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS mime_type ON TABLE kyc_document TYPE option<string>;
DEFINE FIELD IF NOT EXISTS file_size_bytes ON TABLE kyc_document TYPE option<int>;
DEFINE FIELD IF NOT EXISTS metadata ON TABLE kyc_document TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_document TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_document_submission_id
    ON TABLE kyc_document
    FIELDS submission_id;
