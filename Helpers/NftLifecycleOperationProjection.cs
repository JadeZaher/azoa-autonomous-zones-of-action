using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Projects durable NFT evidence onto the existing operation response contract.</summary>
public static class NftLifecycleOperationProjection
{
    /// <summary>Maps the public mint contract to the exact durable issuance request.</summary>
    public static UserNFTMintModel MintModel(NftMintRequest request, NftFeeAuthorization? fallbackFee = null) => new()
    {
        WalletId = request.WalletId, ChainType = request.ChainId, TokenId = request.TokenId,
        MetadataURI = request.MetadataUri ?? string.Empty, Name = request.Name, Description = request.Description,
        ImageURI = request.ImageUri, Attributes = request.Metadata, FeeAuthorization = request.FeeAuthorization ?? fallbackFee,
        ProtocolCostAuthorization = request.ProtocolCostAuthorization,
    };

    /// <summary>Projects confirmed lifecycle evidence without exposing custody or request parameters.</summary>
    public static AZOAResult<IBlockchainOperation> Project(AZOAResult<NftLifecycleView> result, Guid actor, Guid wallet, string chain)
    {
        if (result.IsError || result.Result is null) return AZOAResult<IBlockchainOperation>.Failure(result.Message);
        var view = result.Result;
        if (view.State == "Cancelled") return AZOAResult<IBlockchainOperation>.FailureWithCode("NFT request was cancelled; this key cannot be reused.", "NFT_CANCELLED");
        if (view.State == "Failed") return AZOAResult<IBlockchainOperation>.FailureWithCode(view.Detail ?? "NFT request failed before submission.", "NFT_FAILED");
        var operation = new BlockchainOperation
        {
            Id = Guid.Parse(view.OperationId),
            UserId = actor, WalletId = wallet, OperationType = "NftLifecycle",
            Status = view.State == "Completed" ? OperationStatus.Completed : OperationStatus.PendingConfirmation,
            Parameters = new Dictionary<string, string>
            {
                ["LifecycleId"] = view.OperationId, ["holonId"] = view.NftId.ToString(),
                ["ChainType"] = chain, ["assetId"] = view.AssetId ?? string.Empty,
            },
        };
        if (view.State != "Completed")
            return AZOAResult<IBlockchainOperation>.FailureWithCode(
                $"NFT operation {view.OperationId} is {view.State}; resume the same lifecycle.", "NFT_PENDING", operation);
        if (view.TransactionId is not null) operation.Parameters["TxHash"] = view.TransactionId;
        return AZOAResult<IBlockchainOperation>.Success(operation);
    }
}
