using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("kyb_verification", Aggregate = "Tenant business verification", Guardrail = "G6 SCHEMAFULL")]
[SurrealNote("One current business-verification authority per tenant. Attempt and authority revision CAS prevents stale callbacks deciding a replacement attempt. Provider participant correlations never appear in tenant responses. See Services/Kyb/AGENTS.md.")]
[Slice("identity")]
public sealed partial class KybVerification : ISurrealRecord
{
    public const string SchemaNameConst = "kyb_verification";
    public string SchemaName => SchemaNameConst;
    [Id, Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;
    [Required(NotEmpty = true)]
    public string AttemptId { get; set; } = string.Empty;
    [Required(NotEmpty = true)]
    public string AdmissionKey { get; set; } = string.Empty;
    [Required(NotEmpty = true)]
    public string RequestKeyHash { get; set; } = string.Empty;
    [Required(NotEmpty = true)]
    public string AuthorityRevision { get; set; } = string.Empty;
    public string? HostedSessionId { get; set; }
    [Inside("PENDING", "APPROVED", "REJECTED")]
    public string Status { get; set; } = "PENDING";
    [Default("0")]
    public long Version { get; set; }
    public string? BusinessEvidenceJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}
