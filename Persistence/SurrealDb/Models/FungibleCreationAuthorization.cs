using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable fresh participant authorization for one preserved fungible creation operation.</summary>
[SurrealTable("fungible_creation_authorization", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("fungible_creation_authorization_operation", Fields = new[] { "operation_id", "created_at" })]
public partial class FungibleCreationAuthorization : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_creation_authorization";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public long ExpectedVersion { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string AuthorityJson { get; set; } = string.Empty;
    public string? PreparedHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
