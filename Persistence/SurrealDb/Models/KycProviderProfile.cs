// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("kyc_provider_profile",
    Aggregate = "Node-scoped KYC provider catalog",
    Guardrail = "Operator-owned non-secret configuration; versioned CAS")]
[SurrealNote("Secrets remain in the deployment secret store. Tenants select only enabled, runtime-ready profile keys.")]
[Slice("identity")]
public partial class KycProviderProfile : ISurrealRecord
{
    public const string SchemaNameConst = "kyc_provider_profile";
    public string SchemaName => SchemaNameConst;

    [Id, Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Required(NotEmpty = true)]
    public string DisplayName { get; set; } = string.Empty;

    [Required(NotEmpty = true)]
    public string AdapterKey { get; set; } = string.Empty;

    [Default("false")]
    public bool Enabled { get; set; }

    [Required(NotEmpty = true)]
    public string PolicyVersion { get; set; } = string.Empty;

    [Required(NotEmpty = true)]
    public string AssuranceLevel { get; set; } = string.Empty;

    [Default("1")]
    public long Version { get; set; } = 1;

    [Default("1")]
    public long TrustRevision { get; set; } = 1;

    [References(typeof(Avatar))]
    public string UpdatedByAvatarId { get; set; } = string.Empty;

    [Default("time::now()"), ReadOnly]
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
