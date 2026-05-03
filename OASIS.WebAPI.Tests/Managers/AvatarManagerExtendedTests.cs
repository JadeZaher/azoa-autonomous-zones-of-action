using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

public class AvatarManagerExtendedTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly ProviderContext _providerContext;
    private readonly AvatarManager _manager;

    public AvatarManagerExtendedTests()
    {
        _provider = new Mock<IOASISStorageProvider>();
        _provider.Setup(p => p.ProviderName).Returns("InMemory");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OASIS:DefaultProvider"] = "InMemory",
                ["Jwt:Key"] = "super-secret-key-for-testing-only!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        _providerContext = new ProviderContext(new[] { _provider.Object }, config, null);
        _manager = new AvatarManager(_providerContext, config);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllAvatars()
    {
        _provider.Setup(p => p.LoadAllAvatarsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IAvatar>>
                 {
                     Result = new[] { new Avatar { Username = "a1" }, new Avatar { Username = "a2" } }
                 });

        var result = await _manager.GetAllAsync();

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateAsync_WithMissingAvatar_ShouldReturnError()
    {
        _provider.Setup(p => p.LoadAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IAvatar> { IsError = true, Message = "Not found" });

        var result = await _manager.UpdateAsync(Guid.NewGuid(), new AvatarUpdateModel { FirstName = "X" });

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_WithPartialFields_ShouldOnlyUpdateProvided()
    {
        var avatar = new Avatar
        {
            Id = Guid.NewGuid(),
            Username = "keep",
            Email = "keep@test.com",
            FirstName = "Old",
            LastName = "Name",
            IsActive = true
        };
        _provider.Setup(p => p.LoadAvatarAsync(avatar.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IAvatar> { Result = avatar });
        _provider.Setup(p => p.SaveAvatarAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IAvatar a, CancellationToken _) => new OASISResult<IAvatar> { Result = a });

        var result = await _manager.UpdateAsync(avatar.Id, new AvatarUpdateModel { FirstName = "New" });

        result.IsError.Should().BeFalse();
        result.Result!.FirstName.Should().Be("New");
        result.Result.Username.Should().Be("keep");
        result.Result.LastName.Should().Be("Name");
        result.Result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnProviderResult()
    {
        _provider.Setup(p => p.DeleteAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveWalletAsync_WhenWalletMissing_ShouldReturnError()
    {
        _provider.Setup(p => p.LoadWalletAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { IsError = true, Message = "Not found" });

        var result = await _manager.RemoveWalletAsync(Guid.NewGuid(), Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetWalletsAsync_ShouldReturnWallets()
    {
        var avatarId = Guid.NewGuid();
        _provider.Setup(p => p.LoadWalletsByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IWallet>>
                 {
                     Result = new[] { new Wallet { Address = "addr1" }, new Wallet { Address = "addr2" } }
                 });

        var result = await _manager.GetWalletsAsync(avatarId);

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task RegisterAsync_WhenProviderActivationFails_ShouldReturnError()
    {
        var config = new ConfigurationBuilder().Build();
        var emptyContext = new ProviderContext(Array.Empty<IOASISStorageProvider>(), config, null);
        var manager = new AvatarManager(emptyContext, config);

        var result = await manager.RegisterAsync(new AvatarRegisterModel { Username = "x", Email = "x@test.com", Password = "pass" });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("No storage provider available");
    }
}
