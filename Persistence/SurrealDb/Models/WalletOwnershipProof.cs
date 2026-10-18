using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Latest signature-verified control proof per canonical (chain, address); the record id is the lock.</summary>
[SurrealTable("wallet_ownership_proof",
    Aggregate = "WalletOwnershipProof (Models/WalletOwnershipProof.cs)",
    Guardrail = "G6 SCHEMAFULL")]
[SurrealNote("Record id = sha256(chain:canonical address): one current prover per address; a newer verified proof supersedes. Written only by the wallet ownership-proof route after a consumed wallet-auth challenge verifies.")]
[Slice("wallet_nft")]
[Index("wallet_ownership_proof_user", Fields = new[] { "user_id" })]
public partial class WalletOwnershipProof : ISurrealRecord
{
    public const string SchemaNameConst = "wallet_ownership_proof";
    public string SchemaName => SchemaNameConst;

    [Id] public string Id { get; set; } = string.Empty;

    [References(typeof(User))]
    public string UserId { get; set; } = string.Empty;

    [Required(NotEmpty = true)] public string ChainType { get; set; } = string.Empty;

    [Required(NotEmpty = true)] public string Address { get; set; } = string.Empty;

    [Required(NotEmpty = true)] public string WalletId { get; set; } = string.Empty;

    public DateTimeOffset ProvenAt { get; set; }
}
