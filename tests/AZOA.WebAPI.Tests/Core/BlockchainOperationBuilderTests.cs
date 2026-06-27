using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Tests.Core;

public class BlockchainOperationBuilderTests
{
    [Fact]
    public void Build_Default_ShouldCreateOperation()
    {
        var builder = new BlockchainOperationBuilder();
        var op = builder.Build();

        op.Should().NotBeNull();
        op.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void ForAvatar_ShouldSetAvatarId()
    {
        var avatarId = Guid.NewGuid();
        var op = new BlockchainOperationBuilder().ForAvatar(avatarId).Build();

        op.AvatarId.Should().Be(avatarId);
    }

    [Fact]
    public void UsingWallet_ShouldSetWalletId()
    {
        var walletId = Guid.NewGuid();
        var op = new BlockchainOperationBuilder().UsingWallet(walletId).Build();

        op.WalletId.Should().Be(walletId);
    }

    [Fact]
    public void WithStatus_ShouldSetStatus()
    {
        var op = new BlockchainOperationBuilder().WithStatus("Confirmed").Build();

        op.Status.Should().Be("Confirmed");
    }

    [Fact]
    public void WithParameter_ShouldAddToParameters()
    {
        var op = new BlockchainOperationBuilder().WithParameter("key", "value").Build();

        op.Parameters.Should().ContainKey("key").WhoseValue.Should().Be("value");
    }

    [Fact]
    public void Mint_ShouldSetOperationTypeAndFields()
    {
        var op = (BlockchainOperation)new BlockchainOperationBuilder()
            .Mint("ipfs://test", 5, "NFT")
            .Build();

        op.OperationType.Should().Be("Mint");
        op.TokenUri.Should().Be("ipfs://test");
        op.Amount.Should().Be(5);
        op.AssetType.Should().Be("NFT");
        op.Parameters.Should().ContainKey("TokenUri");
        op.Parameters.Should().ContainKey("Amount");
        op.Parameters.Should().ContainKey("AssetType");
    }

    [Fact]
    public void Exchange_ShouldSetOperationTypeAndFields()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var op = (BlockchainOperation)new BlockchainOperationBuilder()
            .Exchange(source, target, "1:2")
            .Build();

        op.OperationType.Should().Be("Exchange");
        op.SourceHolonId.Should().Be(source);
        op.TargetHolonId.Should().Be(target);
        op.ExchangeRate.Should().Be("1:2");
    }

    [Fact]
    public void Transfer_ShouldSetOperationTypeAndFields()
    {
        var source = Guid.NewGuid();
        var op = (BlockchainOperation)new BlockchainOperationBuilder()
            .Transfer(source, "addr123")
            .Build();

        op.OperationType.Should().Be("Transfer");
        op.SourceHolonId.Should().Be(source);
        op.RecipientAddress.Should().Be("addr123");
    }

    [Fact]
    public void AsComposite_ShouldSetCompositeTypeAndSubOps()
    {
        var op = new BlockchainOperationBuilder()
            .AsComposite(
                b => b.Mint("uri1", 1, "NFT"),
                b => b.Transfer(Guid.NewGuid(), "addr"))
            .Build();

        op.OperationType.Should().Be("Composite");
        op.Parameters.Keys.Should().Contain(k => k.StartsWith("SubOp_"));
    }

    [Fact]
    public void Chained_Build_ShouldAccumulateAllValues()
    {
        var avatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var op = (BlockchainOperation)new BlockchainOperationBuilder()
            .ForAvatar(avatarId)
            .UsingWallet(walletId)
            .WithStatus(OperationStatus.Pending)
            .WithParameter("ChainType", "Algorand")
            .Mint("ipfs://token", 1, "NFT")
            .Build();

        op.AvatarId.Should().Be(avatarId);
        op.WalletId.Should().Be(walletId);
        op.Status.Should().Be(OperationStatus.Pending);
        op.Parameters["ChainType"].Should().Be("Algorand");
        op.OperationType.Should().Be("Mint");
    }
}
