// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("tenant_kyc_provider_selection",
    Aggregate = "Tenant-selected KYC authority",
    Guardrail = "One fixed-id row per tenant; versioned CAS")]
[SurrealNote("A selection change invalidates prior attempts and approvals by revision; no provider secret is stored here.")]
[Slice("identity")]
[Index("tenant_kyc_provider_key", Fields = new[] { "provider_key" })]
public partial class TenantKycProviderSelection : ISurrealRecord
{
    public const string SchemaNameConst = "tenant_kyc_provider_selection";
    public string SchemaName => SchemaNameConst;

    [Id, Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [References(typeof(Avatar))]
    public string TenantId { get; set; } = string.Empty;

    [Required(NotEmpty = true)]
    public string ProviderKey { get; set; } = string.Empty;

    [Default("1")]
    public long SelectionVersion { get; set; } = 1;

    [References(typeof(Avatar))]
    public string UpdatedByAvatarId { get; set; } = string.Empty;

    [Default("time::now()"), ReadOnly]
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
