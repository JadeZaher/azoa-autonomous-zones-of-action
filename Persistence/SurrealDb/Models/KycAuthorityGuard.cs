// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_authority_guard table.

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("kyc_authority_guard",
    Aggregate = "KycAuthorityGuard (Persistence/SurrealDb/Models/KycAuthorityGuard.cs)",
    Guardrail = "G6 SCHEMAFULL; concurrency-revision guard, not a value ledger")]
[SurrealNote("OI-76: this table predates the attribute-schema system -- it was created by a hand-written migration and had no attributed POCO, so the byte-equivalence guard (which iterates POCOs, not goldens) never saw it. Promoted here so SchemaGoldenOrphanTests stops needing to allowlist it. The committed .surql at Generated/Schemas/kyc_authority_guard.surql was hand-formatted (single-line DEFINE FIELD statements, inline INSIDE array, no banner); it MUST be regenerated via AZOA_REGENERATE_GOLDENS=1 (see tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs) so the tool-emitted form becomes the new golden. Regeneration is NOT purely cosmetic and an earlier draft of this note wrongly said it was: the emitter also lifts the inline INSIDE array into a table-scoped DEFINE PARAM $kyc_authority_guard_kind, the convention 44 of the 70 committed goldens already use. The ADMITTED VALUES are identical, and every statement carries IF NOT EXISTS, so a database created by the original migration keeps the assert it already has.")]
[SurrealNote("Three Id shapes share this table by convention, not by a discriminated Id type: 'provider-{ProviderKey}', 'tenant-{TenantKycProviderSelectionId}', and 'attempt-{UserId}-{TenantId|\"direct\"}'. Kind mirrors which shape produced the row; callers must not assume Id encodes Kind for any other purpose.")]
[SurrealNote("Written by SurrealKycStore and SurrealKycControlStore on every provider/tenant KYC-authority change and every submission attempt. The 'attempt' row's AuthorityRevision is read back in the same UPSERT (math::max(authority_revision ?? 0, 0) + 1) to serialize concurrent submissions for the same (user, tenant) pair -- this is a revision counter for detecting stale authority, never a balance or an audit trail of value moved.")]
[Slice("identity")]
public partial class KycAuthorityGuard : ISurrealRecord
{
    public const string SchemaNameConst = "kyc_authority_guard";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Which authority this row guards: "provider", "tenant", or "attempt".</summary>
    [Column(Order = 2)]
    [Inside("provider", "tenant", "attempt")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Monotonic revision counter, bumped on every write to the guarded authority.</summary>
    [Column(Order = 3)]
    [Default("0")]
    public long AuthorityRevision { get; set; }

    /// <summary>The KYC provider key this guard tracks (absent is never written by current callers).</summary>
    [Column(Order = 4)]
    public string? ProviderKey { get; set; }

    /// <summary>Last write time (UTC); the guard row does not otherwise version its history.</summary>
    [Column(Order = 5)]
    [Default("time::now()")]
    public DateTimeOffset TouchedAt { get; set; }
}
