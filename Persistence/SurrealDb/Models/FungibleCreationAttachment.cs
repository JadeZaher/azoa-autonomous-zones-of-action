using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Current Holon owner's exact acceptance of an already-finalized issuance association.</summary>
[SurrealTable("fungible_creation_attachment", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("fungible_creation_attachment_operation", Fields = new[] { "operation_id" }, Unique = true)]
public partial class FungibleCreationAttachment : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_creation_attachment";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ProjectionAuthorityId { get; set; } = string.Empty;
    public string HolonId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public long ExpectedVersion { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string ConsentJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
