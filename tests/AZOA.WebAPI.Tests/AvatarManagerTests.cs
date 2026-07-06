using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests;

public class AvatarManagerTests
{
    private readonly Mock<IAvatarStore> _store;
    private readonly AvatarManager _manager;

    public AvatarManagerTests()
    {
        _store = new Mock<IAvatarStore>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:DefaultProvider"] = "InMemory",
                ["Jwt:Key"] = "super-secret-key-for-testing-only!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns("Development");
        _manager = new AvatarManager(_store.Object, config, environment.Object);
    }

    [Fact]
    public async Task RegisterAsync_ShouldHashPasswordAndSave()
    {
        // RegisterAsync now checks for duplicate email/username first, so the
        // avatar listing must be mocked or the loop NREs on a null result.
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = Array.Empty<IAvatar>() });

        _store.Setup(p => p.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

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

        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new[] { avatar } });

        var model = new AvatarLoginModel { Email = "test@test.com", Password = "password123" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoginAsync_WithInvalidCredentials_ShouldReturnError()
    {
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = Array.Empty<IAvatar>() });

        var model = new AvatarLoginModel { Email = "test@test.com", Password = "wrong" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid credentials");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnAvatar()
    {
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "test" };
        _store.Setup(p => p.GetByIdAsync(avatar.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IAvatar> { Result = avatar });

        var result = await _manager.GetAsync(avatar.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Username.Should().Be("test");
    }

}
