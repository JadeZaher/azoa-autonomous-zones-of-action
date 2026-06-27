using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Blockchain;

public class BlockchainOperationBuilder
{
    private readonly BlockchainOperation _operation = new();

    public BlockchainOperationBuilder ForAvatar(Guid avatarId)
    {
        _operation.AvatarId = avatarId;
        return this;
    }

    public BlockchainOperationBuilder UsingWallet(Guid walletId)
    {
        _operation.WalletId = walletId;
        return this;
    }

    public BlockchainOperationBuilder WithStatus(string status)
    {
        _operation.Status = status;
        return this;
    }

    public BlockchainOperationBuilder WithParameter(string key, string value)
    {
        _operation.Parameters[key] = value;
        return this;
    }

    public BlockchainOperationBuilder Mint(string tokenUri, ulong amount, string assetType)
    {
        _operation.OperationType = "Mint";
        _operation.TokenUri = tokenUri;
        _operation.Amount = amount;
        _operation.AssetType = assetType;
        _operation.Parameters["TokenUri"] = tokenUri;
        _operation.Parameters["Amount"] = amount.ToString();
        _operation.Parameters["AssetType"] = assetType;
        return this;
    }

    public BlockchainOperationBuilder Exchange(Guid sourceHolonId, Guid targetHolonId, string exchangeRate)
    {
        _operation.OperationType = "Exchange";
        _operation.SourceHolonId = sourceHolonId;
        _operation.TargetHolonId = targetHolonId;
        _operation.ExchangeRate = exchangeRate;
        _operation.Parameters["SourceHolonId"] = sourceHolonId.ToString();
        _operation.Parameters["TargetHolonId"] = targetHolonId.ToString();
        _operation.Parameters["ExchangeRate"] = exchangeRate;
        return this;
    }

    public BlockchainOperationBuilder Transfer(Guid sourceHolonId, string recipientAddress)
    {
        _operation.OperationType = "Transfer";
        _operation.SourceHolonId = sourceHolonId;
        _operation.RecipientAddress = recipientAddress;
        _operation.Parameters["SourceHolonId"] = sourceHolonId.ToString();
        _operation.Parameters["RecipientAddress"] = recipientAddress;
        return this;
    }

    public BlockchainOperationBuilder AsComposite(params Action<BlockchainOperationBuilder>[] steps)
    {
        _operation.OperationType = "Composite";
        foreach (var step in steps)
        {
            var subBuilder = new BlockchainOperationBuilder();
            step(subBuilder);
            var subOp = subBuilder.Build();
            _operation.Parameters[$"SubOp_{subOp.Id}"] = subOp.OperationType;
        }
        return this;
    }

    public IBlockchainOperation Build() => _operation;
}
