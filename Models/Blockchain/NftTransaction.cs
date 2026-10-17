using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Blockchain;

public sealed record NftTransactionIntent(
    string Kind, string ChainType, ChainNetwork Network, string SignerAddress,
    string RecipientAddress, string? AssetId, string Name, string MetadataUri,
    bool Soulbound, SigningContext SigningContext, string? SourceAddress = null,
    string PreparationKey = "", NftFeeIntent? Fee = null, NftOperationAuthorization? Authorization = null,
    string ContractAddress = "",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NftProtocolCostQuote? ProtocolCostQuote = null);

public sealed record NftOperationAuthorization(
    string LifecycleId, string Operation, string Stage, int PreparationGeneration,
    Guid ActorUserId, string ActorAddress, SigningContext ActorSigningContext,
    Guid? IssuerUserId, string? IssuerAddress, SigningContext? IssuerSigningContext,
    bool IsFinalEffect, NftFeeQuote FeeQuote);

public sealed record PreparedNftTransaction(
    string TransactionId, string SignedTransactionBase64, NftTransactionIntent Intent,
    string ProtocolIdentity, string ContractAddress,
    IReadOnlyList<string>? AdditionalSignedTransactionsBase64 = null, string? AtomicGroupIdentity = null,
    string? RecoveryMetadata = null, string? TokenStandard = null);

public sealed record NftTransactionObservation(
    string Status, string TransactionId, string? AssetId, string? OwnerAddress,
    string ProtocolIdentity, string ContractAddress, string? Detail = null);

public sealed record NftOwnershipObservation(bool Exists, string? OwnerAddress, bool Soulbound);

public sealed record NftTransactionAttempt(PreparedNftTransaction Transaction, string Verdict, DateTimeOffset ObservedAt);

public static class NftTransactionKinds
{
    public const string Create = "Create";
    public const string Accept = "Accept";
    public const string Deliver = "Deliver";
    public const string Transfer = "Transfer";
    public const string Burn = "Burn";
    public const string ReturnForBurn = "ReturnForBurn";
    public const string Revoke = "Revoke";
}
