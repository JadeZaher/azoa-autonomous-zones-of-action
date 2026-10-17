using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Users;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

public sealed class UserNftBindingAccessTests
{
    [Theory]
    [InlineData("read", null, true)]
    [InlineData("read-only", null, true)]
    [InlineData("read", "false", false)]
    [InlineData("full", "false", false)]
    [InlineData("admin", null, false)]
    [InlineData("limited", "true", true)]
    public async Task ReadBindingHonorsNamedLevelAndExplicitDenial(string level, string? explicitValue, bool expected)
    {
        var owner = Guid.NewGuid();
        var nftId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var nft = Mock.Of<IUserNFT>(item => item.Id == nftId && item.IsActive == true && item.UserId == owner);
        var map = new Dictionary<string, string>();
        if (explicitValue is not null) map["read"] = explicitValue;
        var nfts = new Mock<INftStore>();
        nfts.Setup(store => store.GetUserNFTByIdAsync(nftId, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IUserNFT>.Success(nft));
        nfts.Setup(store => store.GetHolonNFTBindingsByUserNFTAsync(nftId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IHolonNFTBinding>>.Success([new HolonNFTBinding
            { UserNFTId = nftId, HolonId = target, PermissionLevel = level, Permissions = map, IsActive = true }]));
        nfts.Setup(store => store.GetWalletNFTBindingsByUserNFTAsync(nftId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWalletNFTBinding>>.Success([new WalletNFTBinding
            { UserNFTId = nftId, WalletId = target, AccessLevel = level, AccessPermissions = map, IsActive = true }]));
        var lifecycle = new Mock<INftLifecycleService>();
        lifecycle.Setup(service => service.IsCurrentOwnerAsync(owner, nftId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new UserNFTService(nfts.Object, Targets(target, owner).Holons, Targets(target, owner).Wallets, lifecycle.Object);
        (await service.VerifyHolonAccessAsync(nftId, target, "read", callerUserId: owner)).Result.Should().Be(expected);
        (await service.VerifyWalletAccessAsync(nftId, target, "read", callerUserId: owner)).Result.Should().Be(expected);
        (await service.VerifyHolonAccessAsync(nftId, target, "read", callerUserId: Guid.NewGuid())).Result.Should().BeFalse();
    }

    [Fact]
    public async Task A_binding_carried_across_a_transfer_grants_nothing_and_cannot_be_raised_by_the_new_holder()
    {
        var previous = Guid.NewGuid(); var holder = Guid.NewGuid(); var nftId = Guid.NewGuid(); var target = Guid.NewGuid();
        var nft = Mock.Of<IUserNFT>(item => item.Id == nftId && item.IsActive == true && item.UserId == holder);
        var binding = new HolonNFTBinding { Id = Guid.NewGuid(), UserNFTId = nftId, HolonId = target, PermissionLevel = "read", IsActive = true };
        var nfts = new Mock<INftStore>();
        nfts.Setup(store => store.GetUserNFTByIdAsync(nftId, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IUserNFT>.Success(nft));
        nfts.Setup(store => store.GetHolonNFTBindingsByUserNFTAsync(nftId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IHolonNFTBinding>>.Success([binding]));
        nfts.Setup(store => store.GetHolonNFTBindingByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolonNFTBinding>.Success(binding));
        nfts.Setup(store => store.DeleteHolonNFTBindingAsync(binding.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
        var lifecycle = new Mock<INftLifecycleService>();
        lifecycle.Setup(service => service.IsCurrentOwnerAsync(holder, nftId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var targets = Targets(target, previous);
        var service = new UserNFTService(nfts.Object, targets.Holons, targets.Wallets, lifecycle.Object);

        (await service.VerifyHolonAccessAsync(nftId, target, "read", callerUserId: holder)).Result.Should().BeFalse();
        (await service.UpdateHolonBindingAsync(binding.Id, new() { PermissionLevel = "full" }, holder)).IsError.Should().BeTrue();
        nfts.Verify(store => store.UpsertHolonNFTBindingAsync(It.IsAny<IHolonNFTBinding>(), It.IsAny<CancellationToken>()), Times.Never);
        (await service.RemoveHolonBindingAsync(binding.Id, previous)).Result.Should().BeTrue("the holon's owner can revoke a binding left behind");
    }

    private static (IHolonStore Holons, IWalletStore Wallets) Targets(Guid target, Guid owner)
    {
        var holons = new Mock<IHolonStore>();
        holons.Setup(store => store.GetByIdAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolon>.Success(new Holon { Id = target, UserId = owner }));
        var wallets = new Mock<IWalletStore>();
        wallets.Setup(store => store.GetByIdAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet { Id = target, UserId = owner }));
        return (holons.Object, wallets.Object);
    }
}
