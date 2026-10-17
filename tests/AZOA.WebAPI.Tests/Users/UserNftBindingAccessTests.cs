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
        var service = new UserNFTService(nfts.Object, Mock.Of<IHolonStore>(), Mock.Of<IWalletStore>(), lifecycle.Object);
        (await service.VerifyHolonAccessAsync(nftId, target, "read", callerUserId: owner)).Result.Should().Be(expected);
        (await service.VerifyWalletAccessAsync(nftId, target, "read", callerUserId: owner)).Result.Should().Be(expected);
        (await service.VerifyHolonAccessAsync(nftId, target, "read", callerUserId: Guid.NewGuid())).Result.Should().BeFalse();
    }
}
