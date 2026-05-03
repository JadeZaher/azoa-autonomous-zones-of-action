using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class AvatarManagerTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly ProviderContext _providerContext;
    private readonly AvatarManager _manager;

    public AvatarManagerTests()
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
    public async Task RegisterAsync_ShouldHashPasswordAndSave()
    {
        _provider.Setup(p => p.SaveAvatarAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IAvatar a, CancellationToken _) => new OASISResult<IAvatar> { Result = a });

        var model = new AvatarRegisterModel
        {
            Username = "testuser",
            Email = "test@test.com",
            Password = "password123"
        };

        var result = await _manager.RegisterAsync(model);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.Username.Should().Be("testuser");
        result.Result.PasswordHash.Should().NotBe("password123");
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ShouldReturnToken()
    {
        var avatar = new Avatar
        {
            Email = "test@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123")
        };

        _provider.Setup(p => p.LoadAllAvatarsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IAvatar>> { Result = new[] { avatar } });

        var model = new AvatarLoginModel { Email = "test@test.com", Password = "password123" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoginAsync_WithInvalidCredentials_ShouldReturnError()
    {
        _provider.Setup(p => p.LoadAllAvatarsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IAvatar>> { Result = Array.Empty<IAvatar>() });

        var model = new AvatarLoginModel { Email = "test@test.com", Password = "wrong" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid credentials");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnAvatar()
    {
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "test" };
        _provider.Setup(p => p.LoadAvatarAsync(avatar.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IAvatar> { Result = avatar });

        var result = await _manager.GetAsync(avatar.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Username.Should().Be("test");
    }

    [Fact]
    public async Task AddWalletAsync_ShouldSetAvatarIdAndSave()
    {
        var wallet = new Wallet { Address = "0x123", ChainType = "Ethereum" };
        _provider.Setup(p => p.SaveWalletAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new OASISResult<IWallet> { Result = w });

        var result = await _manager.AddWalletAsync(Guid.NewGuid(), wallet);

        result.IsError.Should().BeFalse();
        result.Result!.AvatarId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task RemoveWalletAsync_WithWrongAvatar_ShouldReturnError()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid() };
        _provider.Setup(p => p.LoadWalletAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });

        var result = await _manager.RemoveWalletAsync(Guid.NewGuid(), wallet.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned by avatar");
    }
}
