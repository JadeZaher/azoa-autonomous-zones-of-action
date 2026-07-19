// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("kyc_control_audit",
    Aggregate = "Application-append-only KYC control-plane audit",
    Guardrail = "The store exposes create-only operator and tenant configuration evidence")]
[Slice("identity")]
[Index("kyc_control_audit_tenant", Fields = new[] { "tenant_id", "occurred_at" })]
public partial class KycControlAudit : ISurrealRecord
{
    public const string SchemaNameConst = "kyc_control_audit";
    public string SchemaName => SchemaNameConst;

    [Id, Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Required(NotEmpty = true)]
    public string Action { get; set; } = string.Empty;

    [References(typeof(Avatar), Optional = true)]
    public string? TenantId { get; set; }

    [Required(NotEmpty = true)]
    public string ProviderKey { get; set; } = string.Empty;

    public string? PreviousProviderKey { get; set; }

    public long Version { get; set; }

    public string? PreviousDisplayName { get; set; }
    public string? DisplayName { get; set; }
    public string? PreviousAdapterKey { get; set; }
    public string? AdapterKey { get; set; }
    public bool? PreviousEnabled { get; set; }
    public bool? Enabled { get; set; }
    public string? PreviousPolicyVersion { get; set; }
    public string? PolicyVersion { get; set; }
    public string? PreviousAssuranceLevel { get; set; }
    public string? AssuranceLevel { get; set; }
    public long? PreviousTrustRevision { get; set; }
    public long? TrustRevision { get; set; }

    [References(typeof(Avatar))]
    public string ActorAvatarId { get; set; } = string.Empty;

    [Default("time::now()"), ReadOnly]
    public DateTimeOffset OccurredAt { get; set; }
}
