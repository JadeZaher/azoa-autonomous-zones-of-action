namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Current owner acceptance of the original requested attachment and exact finalized asset.</summary>
public sealed record FungibleAttachmentRequest(Guid RequestId, long Version, string ProjectionAuthorityId);

/// <summary>Attachment disclosure excluding the issuer's private wallet and credential details.</summary>
public sealed record FungibleAttachmentView(string OperationId, long Version, string State, Guid HolonId,
    string ProjectionAuthorityId, string ChainType, string Network, string AssetId, string TransactionId);
