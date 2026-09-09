-- ============================================================
-- Table: kyc_authority_guard
-- Aggregate: KycAuthorityGuard (Persistence/SurrealDb/Models/KycAuthorityGuard.cs)
-- Guardrail: G6 SCHEMAFULL; concurrency-revision guard, not a value ledger
-- Note: OI-76: this table predates the attribute-schema system -- it was created by a hand-written migration and had no attributed POCO, so the byte-equivalence guard (which iterates POCOs, not goldens) never saw it. Promoted here so SchemaGoldenOrphanTests stops needing to allowlist it. The committed .surql at Generated/Schemas/kyc_authority_guard.surql was hand-formatted (single-line DEFINE FIELD statements, inline INSIDE array, no banner); it MUST be regenerated via AZOA_REGENERATE_GOLDENS=1 (see tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs) so the tool-emitted form becomes the new golden. Regeneration is NOT purely cosmetic and an earlier draft of this note wrongly said it was: the emitter also lifts the inline INSIDE array into a table-scoped DEFINE PARAM $kyc_authority_guard_kind, the convention 44 of the 70 committed goldens already use. The ADMITTED VALUES are identical, and every statement carries IF NOT EXISTS, so a database created by the original migration keeps the assert it already has.
-- Note: Three Id shapes share this table by convention, not by a discriminated Id type: 'provider-{ProviderKey}', 'tenant-{TenantKycProviderSelectionId}', and 'attempt-{UserId}-{TenantId|"direct"}'. Kind mirrors which shape produced the row; callers must not assume Id encodes Kind for any other purpose.
-- Note: Written by SurrealKycStore and SurrealKycControlStore on every provider/tenant KYC-authority change and every submission attempt. The 'attempt' row's AuthorityRevision is read back in the same UPSERT (math::max(authority_revision ?? 0, 0) + 1) to serialize concurrent submissions for the same (user, tenant) pair -- this is a revision counter for detecting stale authority, never a balance or an audit trail of value moved.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $kyc_authority_guard_kind VALUE ["provider", "tenant", "attempt"];

DEFINE TABLE IF NOT EXISTS kyc_authority_guard SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_authority_guard TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS kind ON TABLE kyc_authority_guard TYPE string
    ASSERT $value INSIDE $kyc_authority_guard_kind;
DEFINE FIELD IF NOT EXISTS authority_revision ON TABLE kyc_authority_guard TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE kyc_authority_guard TYPE option<string>;
DEFINE FIELD IF NOT EXISTS touched_at ON TABLE kyc_authority_guard TYPE datetime
    DEFAULT time::now();
