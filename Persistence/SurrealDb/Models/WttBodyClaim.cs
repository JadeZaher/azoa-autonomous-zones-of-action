using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Permanent target-deployment reservation for one canonical source publication.</summary>
[SurrealTable("wtt_body_claim", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Slice("wtt")]
[Index("wtt_publication_identity", Fields = new[] { "target_partition", "emitter_chain", "emitter_address", "sequence" }, Unique = true)]
public partial class WttBodyClaim : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_body_claim";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string TargetPartition { get; set; } = string.Empty;
    public int TargetProtocolChain { get; set; }
    public string TargetGenesisHex { get; set; } = string.Empty;
    public string TargetCoreBridgeHex { get; set; } = string.Empty;
    public string TargetTokenBridgeHex { get; set; } = string.Empty;
    public string TargetProtocolIdentity { get; set; } = string.Empty;
    public string TargetCoreBridge { get; set; } = string.Empty;
    public string TargetTokenBridge { get; set; } = string.Empty;
    public string SourceProtocolIdentity { get; set; } = string.Empty;
    public int EmitterChain { get; set; }
    public string EmitterAddress { get; set; } = string.Empty;
    public string Sequence { get; set; } = string.Empty;
    public string BodyDigest { get; set; } = string.Empty;
    public string BodyHex { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
