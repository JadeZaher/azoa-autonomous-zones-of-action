using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>One immutable Ethereum signer nonce and its authorized signed candidates.</summary>
[SurrealTable("ethereum_preparation", Aggregate = "Ethereum custody", Guardrail = "G6 SCHEMAFULL")]
[Slice("ethereum_custody")]
[Index("ethereum_preparation_live_nonce", Fields = new[] { "protocol_identity", "sender", "nonce", "nonce_slot" }, Unique = true)]
[Index("ethereum_preparation_owner", Fields = new[] { "owner_user_id", "created_at" })]
public partial class EthereumPreparation : ISurrealRecord
{
    public const string SchemaNameConst = "ethereum_preparation";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string PreparationKey { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    /// <summary>Empty for the one live owner of a nonce; a refilled tombstone holds its own id so it keeps its key lock without the nonce.</summary>
    public string NonceSlot { get; set; } = string.Empty;
    public string CallFingerprint { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string State { get; set; } = "Reserved";
    public string? SignedTransactionBase64 { get; set; }
    public string? TransactionId { get; set; }
    public string RecoveryCandidatesJson { get; set; } = "[]";
    public string RecoveryAuditJson { get; set; } = "[]";
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
