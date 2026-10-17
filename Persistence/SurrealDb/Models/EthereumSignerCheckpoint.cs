using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Explicit clean-chain activation checkpoint for one custody signer.</summary>
[SurrealTable("ethereum_signer_checkpoint", Aggregate = "Ethereum custody", Guardrail = "G6 SCHEMAFULL")]
[Slice("ethereum_custody")]
public partial class EthereumSignerCheckpoint : ISurrealRecord
{
    public const string SchemaNameConst = "ethereum_signer_checkpoint";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string ActivationEpoch { get; set; } = string.Empty;
    public string InitialNonce { get; set; } = string.Empty;
    public string BlockHash { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
