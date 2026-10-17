using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Original participant's durable cancellation of a generation with no authorized chain effect.</summary>
[SurrealTable("fungible_creation_cancellation", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("fungible_creation_cancel_operation", Fields = new[] { "operation_id" }, Unique = true)]
public partial class FungibleCreationCancellation : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_creation_cancellation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string CredentialJson { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
