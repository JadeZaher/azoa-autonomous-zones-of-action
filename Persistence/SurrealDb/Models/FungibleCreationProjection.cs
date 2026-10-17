using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Atomic finalized issuance accounting and the initial optional attachment verdict.</summary>
[SurrealTable("fungible_creation_projection", Aggregate = "Fungible creation", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
public partial class FungibleCreationProjection : ISurrealRecord
{
    public const string SchemaNameConst = "fungible_creation_projection";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string AuthorityId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string? HolonId { get; set; }
    public string InitialAttachmentState { get; set; } = "NotRequested";
    public DateTimeOffset CreatedAt { get; set; }
}
